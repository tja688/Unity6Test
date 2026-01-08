# Unity MCP Integration

This project uses Unity MCP (Model Context Protocol) for Unity Editor operations.

## Available MCP Server

- **unity-mcp**: Provides direct Unity Editor control

## Preferred Approach

When working with Unity:

1. **Use MCP tools first** for runtime scene operations:
   - Creating/deleting GameObjects
   - Adding/removing components
   - Moving/rotating/scaling objects
   - Modifying component properties

2. **Use file operations** only for:
   - Creating new C# scripts
   - Modifying script source code
   - Reading project structure

## MCP Tools Available

The unity-mcp server provides tools like:
- `editor_getHierarchy` - Get current scene hierarchy
- `editor_createEmptyGameObject` - Create new GameObject
- `editor_deleteGameObject` - Delete GameObject
- `editor_setPosition` - Set object position
- `editor_addComponent` - Add component to GameObject
- `editor_executeMenuItem` - Execute Unity menu commands

## Important

- Always check the scene hierarchy using MCP before modifying objects
- MCP operations affect the **live Unity Editor**, not cached file content
- After creating scripts, Unity will recompile - wait for recompilation before adding the script as a component
