# DotnetRuntimeSample

English | [日本語](README_ja.md)

An example .NET implementation of a runtime that can connect to EmptyEngine.

## Modules used

This sample combines the example runtime implementations under Modules.
It has Unity-style GameObjects and components, and loads units composed of them as scenes.

## Editor module references

Each runtime module may have a paired editor-side module.
`EditorHalves.targets` adds references to the editor packages declared in the metadata of the runtime modules.

## Using a DI container

Every element of the game that holds state, including the scene representation, is registered as a service in a single DI container.  
The whole container is disposed and recreated every time you switch between Play and Edit.  
As a result, there is no need for code that rolls back individual pieces of state, nor for verifying such code.  

References from components and assets to services are injected through their constructors.

Only window-related resources cannot be recreated, so they live outside the container.
State such as window position and size carries over across Play/Edit switches.
