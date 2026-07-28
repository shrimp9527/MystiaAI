# MystiaAI

《东方夜雀食堂》（Touhou Mystia's Izakaya）的 AI 对话模组。以 BepInEx 插件形式加载，将游戏中 NPC 的固定闲聊与评价文本替换为大语言模型实时生成的内容，并支持玩家自由输入对话与 AI 推荐回复。
使用人工智能辅助开发(不会C# qwq)有很多乱七八糟不知名的bug，如果你能找到的话欢迎提lssues,我尽力修改
有做其他可以增加角色模组的兼容，但是没有测试过不知道具体这是什么效果

**此模组允许存档中途加入，此模组不修改存档，为了存档安全，还是建议备份一份**
## 功能

- 白天地图上与 NPC 的闲聊对话接入 AI 生成
- 营业时间内稀客、普通客人的闲聊气泡与上菜后评价语接入 AI 生成
- **无上限聊天**：一段对话播完后自动无缝进入下一轮，可以一直聊下去（原理：游戏对话句数固定、无法中途续句，mod 在播完后用同一对话包重开新一轮，并把前几轮完整对话一起发给 AI；两轮衔接处的短暂闪烁是游戏机制限制，并非 bug）
- 玩家可自由输入回复，也可选用 AI 生成的推荐回复选项；输入框旁有「结束对话」按钮，可随时干净结束对话
- 每轮对话固定以 NPC 的话收尾，玩家输入后 NPC 必有回应
- 剧情对话、羁绊升级对话保持游戏原文，不做替换
- 内置 116 条角色人设（稀客 69 条、普通客人 47 条），首次运行自动写入配置文件，可逐角色修改，注意，在编写提示词的时候最好按照默认了提示词的格式进行编写
- 角色 Label（内部名）与 ID 在首次对话后自动回填到人设文件，DLC 角色第一次开口即可命中专属人设
- 结合游戏内时间、当前地图、营业场景、羁绊等级与当日《文文新闻》（标题+正文）作为生成上下文
- 玩家输入提到「报纸/新闻」时，必定注入当日报纸内容，AI 能就报纸作答（可在配置中关闭）
- 生成温度（temperature）可在配置中调整，默认 0.8
- 生成超时或出错时自动回退游戏原文
- 语言自动跟随游戏设置（简中/繁中/英/日/韩）
- 带有配置文件编辑工具，可以在可视化 ui 上修改配置
## 运行环境

- 游戏：Steam 版《东方夜雀食堂》（Unity 2021.3.28f1，IL2CPP 后端）
- 系统：Windows x64
- 加载器：BepInEx 6 IL2CPP 版（BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785 或更新的 bleeding edge 构建，[官方下载页](https://builds.bepinex.dev/projects/bepinex_be)）
- AI 服务：任一 OpenAI 兼容的 chat completions 接口，需自备 API Key
  - 内置预设：DeepSeek（默认）、OpenAI、GLM、月之暗面、Claude
  - 选择 Custom 可接入本地模型（如 Ollama、LM Studio 的兼容端点）

## 安装

1. 下载 BepInEx 6 IL2CPP 版（[BepInEx 官方构建下载页](https://builds.bepinex.dev/projects/bepinex_be)，大多数情况下选择BepInEx-Unity.IL2CPP-win-x64压缩包，不是此系统版本自行判断），解压到游戏根目录，运行一次游戏让其完成初始化
2. 从 [Releases](https://github.com/shrimp9527/MystiaAI/releases) 下载 `MystiaAI.dll`（[最新版直链](https://github.com/shrimp9527/MystiaAI/releases/latest/download/MystiaAI.dll)），放入 `游戏根目录/BepInEx/plugins/`
3. 首次触发 AI 对话后，配置文件生成于 `文档/MystiaAI/`，在 `settings.json` 中填入 API Key 即可

## 配置

配置文件位于 `文档/MystiaAI/`（不放游戏目录，是为了让浏览器可以正常访问）：

- `settings.json`：开关、供应商、API Key、模型、字数上限、超时、温度、报纸话题频率与关键词触发开关等
- `personas.json`：全部角色人设，按中文名组织，可逐角色编辑（Label/ID 由游戏自动回填）
- `aliases.json`：首次对话时一次性预建的角色名映射，一般无需手动修改

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
- 第一原作：ZUN@上海アリス幻樂団

- 第二原作：东方夜雀食堂 / Touhou Mystia’s Izakaya
- [MetaMystia](https://github.com/MetaMikuAI/MetaMystia)：同为《东方夜雀食堂》的模组项目，其源码为本项目理解游戏内部 API 提供了重要佐证
- 角色人设资料整理自 THBWiki 与游戏内文本
- 本项目（游戏 AI 角色设定集）在制作过程中使用了以下网站
-及其提供的数据资料，在此一并表示感谢：

-THBWiki（东方 Project 中文维基）
   https://thbwiki.cc/

   提供了 67 位东方 Project 官方角色的完整词条，
   包括角色信息、生活状况、外貌特征、人际关系、
   官作出场记录及一设资料原文，是本项目
   「角色设定」「角色简介」文件夹的全部一设内容来源。

  -夜雀助手（东方夜雀食堂小助手）
   https://izakaya.cc/

   二创游戏《东方夜雀食堂》的辅助百科，
   提供了游戏中稀客与普客的人物介绍、出没地区、
   预算、喜好标签、符卡效果、点餐与评价对话等
   全部游戏内设定文本。

## 说明

- 本项目为玩家自制模组，与游戏官方及上海爱丽丝幻乐团无关
- AI 生成内容仅替换闲聊与评价类文本，不影响剧情、存档与游戏机制
- 使用 AI 服务产生的费用由使用者与对应服务商结算
