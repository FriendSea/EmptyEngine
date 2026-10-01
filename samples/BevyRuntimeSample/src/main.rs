// Bevy のシーンを描画し、Host の WebSocket ハブを介してエディタと同期する。

use std::collections::HashMap;
use std::env;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::{Arc, Condvar, Mutex};
use std::thread;
use std::time::Duration;

use bevy::ecs::entity::EntityHashMap;
use bevy::ecs::reflect::AppTypeRegistry;
use bevy::prelude::*;
use bevy::scene::serde::SceneDeserializer;
use bevy::scene::DynamicSceneBuilder;
use bevy::window::{WindowPlugin, WindowResolution};
use serde::de::DeserializeSeed;

mod reflect_types;
use reflect_types::{register_reflect_types, ObjectId};

// 接続先の既定値は C# の SceneWire と揃える。
const DEFAULT_HUB_HOST: &str = "127.0.0.1";
const DEFAULT_HUB_PORT: u16 = 5003;
const DEFAULT_HUB_PATH: &str = "/runtime";

// Host のハブが立ち上がるまでの待ち・再接続の間隔。
const RECONNECT_DELAY: Duration = Duration::from_millis(500);

// メッセージ種別は C# の SceneHandling / SceneMessageType と揃える。
const HANDLING_RETAIN: u8 = 1 << 0;
const HANDLING_CANCEL_OPPOSITE: u8 = 1 << 1;

const MSG_INVALIDATE_ASSETS: u8 = 0;
const MSG_EDIT_BLOB: u8 = HANDLING_RETAIN;
const MSG_REQUEST_SCENE: u8 = HANDLING_RETAIN | HANDLING_CANCEL_OPPOSITE;
const MSG_SCENE_BLOB: u8 = HANDLING_CANCEL_OPPOSITE;

// 本体 RON の後ろへ付く付随セクションの区切り。BevySceneText.cs の SceneIdDelimiter /
// ImageSourcesDelimiter と一致させること（本体の後ろへこの順で並ぶ）。
const SCENE_ID_DELIMITER: &str = "\n@@scene_id@@\n";
const IMAGE_SOURCES_DELIMITER: &str = "\n@@image_sources@@\n";

// 付随セクションがシーンインスタンス識別を運んでいなかったときの既定（BevySceneText.cs の
// DefaultSceneName と同じ値）。エディタから届く EditBlob は必ず運ぶので、実際に使うのは
// 手で組んだ blob を流し込んだときだけ。
const DEFAULT_SCENE_ID: &str = "Scene";

// RequestScene の返答が「直前までの EditBlob を適用済みの world」になるのを待つ上限。
// 待つのは高々 1 フレームなので、これに達するのは Bevy 側が止まっているとき。
const APPLY_TIMEOUT: Duration = Duration::from_secs(2);

fn main() {
    // 常駐ランタイムなので、ログを溜め込まず即時に出す。
    let attached = editor_endpoint().is_some();

    println!("[bevy-runtime] EmptyEngine Bevy runtime sample (prototype)");
    println!(
        "[bevy-runtime] editor-attached: {}",
        if attached { "yes" } else { "no" }
    );

    if attached {
        run_attached();
    } else {
        run_standalone();
    }
}

fn run_standalone() {
    let assets = find_imported_assets(&env::current_dir().expect("cwd"))
        .or_else(|| env::args().nth(1).map(PathBuf::from));

    let Some(dir) = assets else {
        eprintln!(
            "[bevy-runtime] ImportedAssets not found. Run the editor once to import, \
             or pass the ImportedAssets path as the first argument."
        );
        std::process::exit(1);
    };

    println!("[bevy-runtime] assets: {}", dir.display());

    let mut scenes = 0;
    for entry in std::fs::read_dir(&dir).expect("read ImportedAssets") {
        let entry = entry.expect("dir entry");
        if !entry.file_type().expect("file type").is_file() {
            continue;
        }
        if entry.file_name() == "startup.json" {
            continue; // 非シーンファイル
        }
        let text = std::fs::read_to_string(entry.path()).expect("read scene file");
        println!(
            "\n--- {} (bevy_scene RON) ---\n{}",
            entry.file_name().to_string_lossy(),
            text.trim()
        );
        scenes += 1;
    }

    println!("\n[bevy-runtime] done ({scenes} artifact file(s) scanned).");
}

fn find_project_root(start: &Path) -> Option<PathBuf> {
    let mut dir = start.to_path_buf();
    loop {
        if dir.join(".artifacts").is_dir() {
            return Some(dir);
        }
        if !dir.pop() {
            return None;
        }
    }
}

fn find_imported_assets(start: &Path) -> Option<PathBuf> {
    find_project_root(start).map(|root| root.join(".artifacts").join("ImportedAssets"))
}

/// 現在のシーンの RON 本文と、反映済みの編集数。
struct LiveScene {
    /// 直近のフレームで書き戻した RON（シーンをロードしていなければ空文字列）
    text: String,
    /// <see cref="LiveScene::text"/> に反映済みの EditBlob の本数
    applied: u64,
    /// 受信スレッドがメインスレッドへ渡した EditBlob の本数
    queued: u64,
}

/// エディタ受信スレッドとシーン更新処理が共有する状態。
type SharedState = Arc<(Mutex<LiveScene>, Condvar)>;

enum EditCommand {
    Edit { is_playing: bool, scene: String },
    Invalidate,
}

/// エディタから受信し、メインスレッドでの適用を待つ要求。
#[derive(Resource)]
struct EditorLink {
    receiver: Mutex<Receiver<EditCommand>>,
    shared: SharedState,
    /// メインスレッドで適用し終えた EditBlob の本数。
    applied: u64,
}

/// エディタが今ランタイムへ預けているシーンインスタンスの識別（`@@scene_id@@` セクション）。
/// ランタイムは中身を解釈せず、poll の返答でそのまま返すだけ。`None` はシーン未ロード。
#[derive(Resource, Default)]
struct SceneInstanceId(Option<String>);

/// インポート済み画像アセットのルートディレクトリ。
#[derive(Resource, Clone)]
struct BevyAssetsDir(PathBuf);

fn run_attached() {
    let shared: SharedState = Arc::new((
        Mutex::new(LiveScene {
            text: String::new(),
            applied: 0,
            queued: 0,
        }),
        Condvar::new(),
    ));
    let (tx, rx) = channel::<EditCommand>();

    {
        let shared = shared.clone();
        thread::spawn(move || editor_client_thread(shared, tx));
    }

    // 取り込んだ GUID 画像を読み込めるよう、エディタの出力先をアセットルートにする。
    let assets_dir = find_project_root(&env::current_dir().expect("cwd"))
        .map(|root| root.join(".artifacts").join("assets"))
        .unwrap_or_else(|| PathBuf::from("assets"));

    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: assets_dir.to_string_lossy().into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "EmptyEngine Bevy runtime sample".to_string(),
                    resolution: WindowResolution::new(960.0, 540.0),
                    ..default()
                }),
                ..default()
            }),
    );

    register_reflect_types(&mut app);

    app.insert_resource(BevyAssetsDir(assets_dir))
        .insert_resource(EditorLink {
            receiver: Mutex::new(rx),
            shared,
            applied: 0,
        })
        .init_resource::<SceneInstanceId>()
        .init_state::<SimState>()
        .add_systems(Startup, spawn_camera)
        .add_systems(
            Update,
            (
                // Edit 復帰直後に一フレーム進むことを防ぐため、シミュレーションをシーン適用より先に行う。
                spin_scene_managed.run_if(in_state(SimState::Play)),
                pump_editor_link,
                // モード不問（C# 版 SerializeScene() と同じく poll は常にライブ world を返す）。
                sync_live_scene_to_shared,
            )
                .chain(),
        )
        .run();
}

/// シミュレーションを停止するエディットモードと、進行するプレイモード。
#[derive(States, Debug, Clone, Copy, Eq, PartialEq, Hash, Default)]
enum SimState {
    #[default]
    Edit,
    Play,
}

fn spawn_camera(mut commands: Commands) {
    commands.spawn(Camera2dBundle::default());
}

/// エディタから読み込まれたシーンに属するエンティティを識別する。
#[derive(Component)]
struct SceneManaged;

/// エディタが指定した画像アセットの GUID。シーンの応答でも同じ参照を返す。
#[derive(Component)]
struct ImageSourceKey(String);

/// エディタから受け取ったエンティティの順序。シーンの応答はこの順序を維持する。
#[derive(Component)]
struct SceneOrder(u32);

// シーン適用には World の排他アクセスが必要なため、exclusive system とする。
fn pump_editor_link(world: &mut World) {
    let commands: Vec<EditCommand> = {
        let link = world.resource::<EditorLink>();
        let receiver = link.receiver.lock().expect("editor link receiver lock");
        let mut drained = Vec::new();
        while let Ok(cmd) = receiver.try_recv() {
            drained.push(cmd);
        }
        drained
    };

    for cmd in commands {
        match cmd {
            EditCommand::Edit { is_playing, scene } => {
                println!(
                    "\n[bevy-runtime] edit received ({} mode, {} bytes RON)",
                    if is_playing { "play" } else { "edit" },
                    scene.len()
                );
                apply_scene_ron(world, &scene);
                world.resource_mut::<EditorLink>().applied += 1;

                // 適用後のシーンがシリアライズされるまで受信スレッドを起こさない。

                // エディタの現在モードへ揃える（Play 専用システムの run_if がこれで開閉する）。
                world
                    .resource_mut::<NextState<SimState>>()
                    .set(if is_playing { SimState::Play } else { SimState::Edit });
            }
            EditCommand::Invalidate => {
                // 再インポート通知。このプロトタイプは解決キャッシュを持たないのでログのみ。
                println!("\n[bevy-runtime] assets invalidated (re-import).");
            }
        }
    }
}

fn apply_scene_ron(world: &mut World, ron_text: &str) {
    let existing: Vec<Entity> = world
        .query_filtered::<Entity, With<SceneManaged>>()
        .iter(world)
        .collect();
    for entity in existing {
        // 親を外してから消す。親子の張られたまま消すと、残った Children が消えた子を指す一瞬ができる。
        world.entity_mut(entity).remove_parent();
        world.despawn(entity);
    }

    // シーンインスタンス識別と GUID 画像アセット参照は bevy_scene RON 本体の外（末尾の付随セクション）に
    // ある。本体だけを SceneDeserializer へ渡す（ファイル冒頭のコメント参照）。
    let (scene_text, scene_id, image_sources) = split_sections(ron_text);

    // 本体が空＝シーン未ロード。識別を落とすことで、以降の poll は「シーンが無い」を返す。
    world.resource_mut::<SceneInstanceId>().0 = (!scene_text.trim().is_empty())
        .then(|| scene_id.unwrap_or_else(|| DEFAULT_SCENE_ID.to_string()));

    if scene_text.trim().is_empty() {
        return;
    }

    let registry = world.resource::<AppTypeRegistry>().clone();
    let dynamic_scene = {
        let type_registry = registry.read();
        let scene_deserializer = SceneDeserializer {
            type_registry: &type_registry,
        };
        let mut ron_deserializer = match ron::de::Deserializer::from_str(scene_text) {
            Ok(d) => d,
            Err(err) => {
                eprintln!("[bevy-runtime] RON parse error: {err}");
                return;
            }
        };
        match scene_deserializer.deserialize(&mut ron_deserializer) {
            Ok(scene) => scene,
            Err(err) => {
                eprintln!("[bevy-runtime] scene deserialize error: {err}");
                return;
            }
        }
    };

    let mut entity_map = EntityHashMap::default();
    if let Err(err) = dynamic_scene.write_to_world(world, &mut entity_map) {
        eprintln!("[bevy-runtime] write_to_world error: {err}");
        return;
    }

    let asset_server = world.resource::<AssetServer>().clone();
    let assets_dir = world.resource::<BevyAssetsDir>().0.clone();

    // RON に含まれない描画必須コンポーネントを補い、応答用に受信順を保持する。
    for (order, dynamic_entity) in dynamic_scene.entities.iter().enumerate() {
        let Some(&new_entity) = entity_map.get(&dynamic_entity.entity) else {
            continue;
        };

        let mut entity_mut = world.entity_mut(new_entity);
        entity_mut.insert((SceneManaged, SceneOrder(order as u32)));
        if !entity_mut.contains::<GlobalTransform>() {
            entity_mut.insert(GlobalTransform::default());
        }
        if !entity_mut.contains::<Visibility>() {
            entity_mut.insert(Visibility::default());
        }
        if !entity_mut.contains::<InheritedVisibility>() {
            entity_mut.insert((InheritedVisibility::default(), ViewVisibility::default()));
        }
        if entity_mut.contains::<Sprite>() && !entity_mut.contains::<Handle<Image>>() {
            // サイドマップに GUID があれば実ハンドルへ解決し、無ければ空のプレースホルダー。
            let resolved = image_sources
                .get(&dynamic_entity.entity.to_bits())
                .and_then(|guid| resolve_image_filename(&assets_dir, guid).map(|f| (guid.clone(), f)));

            match resolved {
                Some((guid, filename)) => {
                    entity_mut.insert(asset_server.load::<Image>(filename));
                    entity_mut.insert(ImageSourceKey(guid));
                }
                None => {
                    entity_mut.insert(Handle::<Image>::default());
                }
            }
        }
    }

    // Parent は write_to_world がエンティティ番号を張り替えて入れてくれるが、対になる Children は
    // 作られない（＝Transform の伝播も可視性の継承もこの木を辿るので、張り直さないと親が効かない）。
    let parented: Vec<(Entity, Entity)> = entity_map
        .values()
        .filter_map(|&entity| Some((entity, world.get::<Parent>(entity)?.get())))
        .collect();
    for (child, parent) in parented {
        // 張り替えが効いていない Parent（＝この world に居ないエンティティ）を渡すと set_parent は落ちる。
        if world.get_entity(parent).is_none() {
            eprintln!("[bevy-runtime] parent {parent:?} is not part of this scene; leaving the child at the root.");
            world.entity_mut(child).remove_parent();
            continue;
        }

        world.entity_mut(child).set_parent(parent);
    }
}

// 付随セクション（BevySceneText.WriteScene 参照）を本体 RON から切り離してパースする。セクションは
// 本体の後ろへ scene_id → image_sources の順で並ぶ。どちらも無ければ全体が本体。
fn split_sections(ron_text: &str) -> (&str, Option<String>, HashMap<u64, String>) {
    let scene_id_at = ron_text.find(SCENE_ID_DELIMITER);
    let images_at = ron_text.find(IMAGE_SOURCES_DELIMITER);

    let body_end = scene_id_at.or(images_at).unwrap_or(ron_text.len());
    let scene_id_end = images_at.unwrap_or(ron_text.len());

    let scene_id = scene_id_at.and_then(|at| {
        ron::de::from_str::<String>(&ron_text[at + SCENE_ID_DELIMITER.len()..scene_id_end]).ok()
    });
    let image_sources = images_at
        .and_then(|at| ron::de::from_str(&ron_text[at + IMAGE_SOURCES_DELIMITER.len()..]).ok())
        .unwrap_or_default();

    (&ron_text[..body_end], scene_id, image_sources)
}

// assets_dir 内で "<guid>.*" に一致する最初のファイル名を返す。拡張子はインポート時の元ファイル依存
// （BevyAssetSerializer 参照）なので固定リストを持たず、ディレクトリを探して見つける。
fn resolve_image_filename(assets_dir: &Path, guid: &str) -> Option<String> {
    let prefix = format!("{guid}.");
    std::fs::read_dir(assets_dir)
        .ok()?
        .filter_map(Result::ok)
        .find_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            name.starts_with(&prefix).then_some(name)
        })
}

/// プレイ中の 1 秒あたりの回転角（ラジアン）。
const SPIN_RADIANS_PER_SEC: f32 = 0.6;

/// プレイ中のシーンオブジェクトを回転させる。エディットモードでは回転させない。
fn spin_scene_managed(time: Res<Time>, mut query: Query<&mut Transform, With<SceneManaged>>) {
    let delta = time.delta_seconds();
    for mut transform in &mut query {
        transform.rotate_z(SPIN_RADIANS_PER_SEC * delta);
    }
}

/// 現在のシーン状態を、エディタへの応答用データへ反映する。モードに関わらず毎フレーム更新する。
fn sync_live_scene_to_shared(world: &mut World) {
    // 空文字列はシーン未ロードを意味するため、失敗時は直前の状態を保持する。
    let Some(ron_text) = serialize_scene_managed(world) else {
        return;
    };

    let link = world.resource::<EditorLink>();
    let applied = link.applied;
    let (live, caught_up) = &*link.shared;

    let mut state = live.lock().expect("scene state lock");
    state.text = ron_text;
    state.applied = applied;
    caught_up.notify_all();
}

/// エディタから読み込まれたエンティティを受信時の順序で RON に変換する。
/// 画像アセットの GUID は付随セクションに含める。
fn serialize_scene_managed(world: &mut World) -> Option<String> {
    // シーンを預かっていなければ、返すのは「エンティティ 0 個のシーン」ではなく「シーンが無い」。
    let Some(scene_id) = world.resource::<SceneInstanceId>().0.clone() else {
        return Some(String::new());
    };

    let mut ordered: Vec<(u32, Entity)> = world
        .query_filtered::<(&SceneOrder, Entity), With<SceneManaged>>()
        .iter(world)
        .map(|(order, entity)| (order.0, entity))
        .collect();
    ordered.sort_unstable();
    let entities: Vec<Entity> = ordered.iter().map(|&(_, entity)| entity).collect();
    let ordered_lookup: Vec<(Entity, u32)> = ordered
        .into_iter()
        .map(|(order, entity)| (entity, order))
        .collect();

    let registry = world.resource::<AppTypeRegistry>().clone();
    // 自動生成されたコンポーネントが編集ツリーへ混入しないよう、返却する型を限定する。
    let scene = DynamicSceneBuilder::from_world(world)
        .deny_all()
        .allow::<Transform>()
        .allow::<Sprite>()
        .allow::<ObjectId>()
        .allow::<Name>()
        .allow::<Visibility>()
        .allow::<Parent>()
        .extract_entities(entities.iter().copied())
        .build();

    // build() は受信順を保持しないため、エディタの表示順へ戻す。
    let mut scene = scene;
    let order_of: HashMap<Entity, u32> = ordered_lookup.into_iter().collect();
    scene
        .entities
        .sort_by_key(|entity| order_of.get(&entity.entity).copied().unwrap_or(u32::MAX));

    let scene_text = {
        let type_registry = registry.read();
        match scene.serialize(&type_registry) {
            Ok(text) => text,
            Err(err) => {
                eprintln!("[bevy-runtime] live scene serialize error: {err}");
                return None;
            }
        }
    };

    let mut image_sources: HashMap<u64, String> = HashMap::new();
    for &entity in &entities {
        if let Some(key) = world.get::<ImageSourceKey>(entity) {
            image_sources.insert(entity.to_bits(), key.0.clone());
        }
    }

    let mut text = match ron::ser::to_string(&scene_id) {
        Ok(quoted) => format!("{scene_text}{SCENE_ID_DELIMITER}{quoted}"),
        Err(err) => {
            eprintln!("[bevy-runtime] scene id serialize error: {err}");
            return None;
        }
    };

    if image_sources.is_empty() {
        return Some(text);
    }

    match ron::ser::to_string(&image_sources) {
        Ok(side_map_ron) => {
            text.push_str(IMAGE_SOURCES_DELIMITER);
            text.push_str(&side_map_ron);
        }
        Err(err) => eprintln!("[bevy-runtime] image source side-map serialize error: {err}"),
    }

    Some(text)
}

fn editor_client_thread(shared: SharedState, tx: Sender<EditCommand>) {
    let endpoint = hub_endpoint();
    println!("[bevy-runtime] dialing editor host {endpoint}...");

    // Host の起動待ちや再起動から復帰できるよう、再接続を続ける。
    loop {
        match tungstenite::connect(&endpoint) {
            Ok((mut socket, _)) => {
                println!("[bevy-runtime] connected to editor host.");
                serve_client(&mut socket, &shared, &tx);
                println!("[bevy-runtime] editor host disconnected. reconnecting...");
            }
            Err(_) => thread::sleep(RECONNECT_DELAY),
        }
    }
}

// Host がハブの接続先を EmptyEngineEditorWebSocketUrl の値として渡す（"ws://host:port/path"）。
// 名乗りが無い／ws でないときは規約の既定へ落とす。
fn hub_endpoint() -> String {
    editor_endpoint().unwrap_or_else(|| {
        format!("ws://{DEFAULT_HUB_HOST}:{DEFAULT_HUB_PORT}{DEFAULT_HUB_PATH}")
    })
}

// 環境変数が ws/wss の URL を名乗っていればそれを返す。エディタアタッチ判定もこれで決まる
// （C# の RuntimeMode.EditorWebSocketEndpoint と同じ規約）。
fn editor_endpoint() -> Option<String> {
    let text = env::var("EmptyEngineEditorWebSocketUrl").ok()?;
    let trimmed = text.trim();
    if trimmed.starts_with("ws://") || trimmed.starts_with("wss://") {
        Some(trimmed.to_string())
    } else {
        None
    }
}

// 1 つのエディタ接続を捌く。
fn serve_client<S>(socket: &mut tungstenite::WebSocket<S>, shared: &SharedState, tx: &Sender<EditCommand>)
where
    S: Read + Write,
{
    loop {
        let Some((msg_type, payload)) = read_message(socket) else {
            break; // 切断
        };

        match msg_type {
            MSG_REQUEST_SCENE => {
                // エディタがランタイムの現在シーンを問い合わせる（ポーリング）。応答はライブ world の RON そのもの。
                let text = live_scene_after_pending_edits(shared);
                if write_message(socket, MSG_SCENE_BLOB, text.as_bytes()).is_err() {
                    break;
                }
            }
            MSG_EDIT_BLOB => {
                // 先頭 1 バイト＝モード（1=プレイ/0=エディット）、残りがシーン RON。
                if !payload.is_empty() {
                    let is_playing = payload[0] != 0;
                    let scene = String::from_utf8_lossy(&payload[1..]).into_owned();
                    // 適用は投げっぱなし（次の pump システムがメインスレッドで実行する）。渡した本数だけ
                    // 控えておき、この後の RequestScene はこれが適用され切るまで答えない。
                    shared.0.lock().expect("scene state lock").queued += 1;
                    let _ = tx.send(EditCommand::Edit { is_playing, scene });
                }
            }
            MSG_INVALIDATE_ASSETS => {
                let _ = tx.send(EditCommand::Invalidate);
            }
            other => {
                println!(
                    "[bevy-runtime] unknown message type {other} ({} bytes), ignored.",
                    payload.len()
                );
            }
        }
    }
}

// 送信済みの編集が古い応答で巻き戻らないよう、適用完了を待ってから返す。
fn live_scene_after_pending_edits(shared: &SharedState) -> String {
    let (live, caught_up) = &**shared;
    let mut state = live.lock().expect("scene state lock");

    while state.applied < state.queued {
        let (next, wait) = caught_up
            .wait_timeout(state, APPLY_TIMEOUT)
            .expect("scene state lock");
        state = next;
        if wait.timed_out() {
            eprintln!(
                "[bevy-runtime] answering with a snapshot older than the last edit \
                 ({} of {} applied); is the app frozen?",
                state.applied, state.queued
            );
            break;
        }
    }

    state.text.clone()
}

fn read_message<S>(socket: &mut tungstenite::WebSocket<S>) -> Option<(u8, Vec<u8>)>
where
    S: Read + Write,
{
    loop {
        // Ping/Pong は tungstenite が自動で返す。テキストは規約外なので黙って読み飛ばす。
        let frame = match socket.read().ok()? {
            tungstenite::Message::Binary(bytes) => bytes,
            tungstenite::Message::Close(_) => return None,
            _ => continue,
        };

        if frame.len() < 5 {
            return None;
        }

        let msg_type = frame[0];
        let length = u32::from_le_bytes(frame[1..5].try_into().unwrap()) as usize;
        if frame.len() != 5 + length {
            return None;
        }

        return Some((msg_type, frame[5..].to_vec()));
    }
}

fn write_message<S>(
    socket: &mut tungstenite::WebSocket<S>,
    msg_type: u8,
    payload: &[u8],
) -> Result<(), tungstenite::Error>
where
    S: Read + Write,
{
    let mut frame = Vec::with_capacity(5 + payload.len());
    frame.push(msg_type);
    frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    frame.extend_from_slice(payload);

    socket.write(tungstenite::Message::Binary(frame))?;
    socket.flush()
}
