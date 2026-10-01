// ランタイムとカタログで登録漏れが生じないよう、型の登録を共有する。

// reflect(Component) の展開がこの名前を参照する。
use bevy::ecs::reflect::ReflectComponent;

/// シーンの再適用やエンティティの並べ替え後も維持する、エディタ用のオブジェクト ID。
#[derive(bevy::prelude::Component, bevy::reflect::Reflect, Default)]
#[reflect(Component)]
#[type_path = "emptyengine"]
pub(crate) struct ObjectId(pub String);

pub(crate) fn register_reflect_types<R: ReflectTypeRegistrar>(target: &mut R) {
    target.register::<bevy::prelude::Transform>();
    target.register::<bevy::prelude::Sprite>();
    // プラグインの構成に依存せずシーンを復元できるよう、ヒエラルキーの属性を登録する。
    target.register::<bevy::prelude::Name>();
    target.register::<bevy::prelude::Visibility>();
    target.register::<bevy::prelude::Parent>();
    target.register::<ObjectId>();
    target.register::<bevy::prelude::Color>();
    target.register::<bevy::prelude::Vec2>();
    target.register::<bevy::prelude::Vec3>();
    target.register::<bevy::prelude::Quat>();
    target.register::<Option<bevy::prelude::Vec2>>();
}

/// ランタイムとスキーマ生成で使う型を登録する契約。
pub(crate) trait ReflectTypeRegistrar {
    fn register<T: bevy::reflect::GetTypeRegistration>(&mut self);
}

impl ReflectTypeRegistrar for bevy::app::App {
    fn register<T: bevy::reflect::GetTypeRegistration>(&mut self) {
        bevy::app::App::register_type::<T>(self);
    }
}

impl ReflectTypeRegistrar for bevy::reflect::TypeRegistry {
    fn register<T: bevy::reflect::GetTypeRegistration>(&mut self) {
        bevy::reflect::TypeRegistry::register::<T>(self);
    }
}
