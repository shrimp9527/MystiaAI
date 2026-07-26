# MystiaAI

《东方夜雀食堂》（Touhou Mystia's Izakaya）的 AI 对话模组。以 BepInEx 插件形式加载，将游戏中 NPC 的固定闲聊与评价文本替换为大语言模型实时生成的内容，并支持玩家自由输入对话与 AI 推荐回复。

## 功能

- 白天地图上与 NPC 的闲聊对话接入 AI 生成
- 营业时间内稀客、普通客人的闲聊气泡与上菜后评价语接入 AI 生成
- 玩家可自由输入回复，也可选用 AI 生成的推荐回复选项
- 剧情对话、羁绊升级对话保持游戏原文，不做替换
- 内置 116 条角色人设（稀客 69 条、普通客人 47 条），首次运行自动写入配置文件，可逐角色修改
- 结合游戏内时间、营业场景、羁绊等级与当日《文文新闻》作为生成上下文
- 生成超时或出错时自动回退游戏原文，并在屏幕角落标注
- 语言自动跟随游戏设置（简中/繁中/英/日/韩）

## 运行环境

- 游戏：Steam 版《东方夜雀食堂》（Unity 2021.3.28f1，IL2CPP 后端）
- 系统：Windows x64
- 加载器：BepInEx 6 IL2CPP 版（BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785 或更新的 bleeding edge 构建）
- AI 服务：任一 OpenAI 兼容的 chat completions 接口，需自备 API Key
  - 内置预设：DeepSeek（默认）、OpenAI、GLM、月之暗面、Claude
  - 选择 Custom 可接入本地模型（如 Ollama、LM Studio 的兼容端点）

## 安装

1. 下载 BepInEx 6 IL2CPP 版，解压到游戏根目录，运行一次游戏让其完成初始化
2. 从 [Releases](https://github.com/shrimp9527/MystiaAI/releases) 下载 `MystiaAI.dll`，放入 `游戏根目录/BepInEx/plugins/`
3. 从 Steam 启动游戏（请保持 Steam 客户端在后台运行，直接双击 exe 可能因 Steam API 初始化失败而闪退）
4. 首次触发 AI 对话后，配置文件生成于 `文档/MystiaAI/`，在 `settings.json` 中填入 API Key 即可

## 配置

配置文件位于 `文档/MystiaAI/`（不放游戏目录，是为了让浏览器可以正常访问）：

- `settings.json`：开关、供应商、API Key、模型、字数上限、超时等
- `personas.json`：全部角色人设，按中文名组织，可逐角色编辑
- `aliases.json`：运行时自动学习的角色名映射，一般无需手动修改

推荐使用网页配置工具编辑（Chrome / Edge 可直接写回文件夹）：

- 在线版：https://shrimp9527.github.io/MystiaAI/
- 打开后选择 `文档/MystiaAI` 文件夹即可读取与保存，游戏内 2 秒内自动热重载

## 从源码构建

1. 安装 .NET 6 SDK
2. 确认本机已按上述方式安装游戏与 BepInEx（csproj 默认引用游戏目录下的 BepInEx 程序集，可用 `/p:GameDir=...` 覆盖）
3. `dotnet build`，构建产物会自动复制到 `BepInEx/plugins/`

## 使用的开源项目

- [BepInEx](https://github.com/BepInEx/BepInEx)（LGPL-2.1）：插件加载器，本模组以其 IL2CPP 版本为运行宿主
- [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop)（MIT）：IL2CPP 与托管代码互操作层，随 BepInEx 6 提供
- [Harmony](https://github.com/pardeike/Harmony)（MIT）：运行时方法补丁框架，模组全部注入点均通过其实现
- [ilspycmd](https://github.com/icsharpcode/ILSpy)（MIT）：开发期用于分析 Il2CppInterop 生成的程序集存根，梳理游戏 API

## 参考与致谢

- [MetaMystia](https://github.com/MetaMikuAI/MetaMystia)：同为《东方夜雀食堂》的模组项目，其源码为本项目理解游戏内部 API 提供了重要佐证
- 角色人设资料整理自 THBWiki 与游戏内文本

## 说明

- 本项目为玩家自制模组，与游戏官方及上海爱丽丝幻乐团无关
- AI 生成内容仅替换闲聊与评价类文本，不影响剧情、存档与游戏机制
- 使用 AI 服务产生的费用由使用者与对应服务商结算
