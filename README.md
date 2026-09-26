# Lucid Cats Chat（文字聊天模组 / Text Chat Mod）

为 Steam 游戏《Lucid Cats》添加文字聊天的 BepInEx 模组。
A BepInEx mod that adds text chat to Lucid Cats.

---

## 功能 / Features

- 进入游戏场景（GameScene）后才显示
- 常驻隐藏，按回车唤起聊天框
- 支持中文输入（IME）
- 联机广播（Netcode for GameObjects）
- 显示玩家真实 Steam 名
- 打字时自动禁用游戏 WASD 输入

---

## 前置 / Requirements

**BepInEx 5 x64**（5.4.23.5 或更新）

- 下载地址：https://github.com/BepInEx/BepInEx/releases
- 本模组不包含 BepInEx，请先自行安装。

---

## 安装 / Installation

1. 确保已安装 BepInEx 5 x64（以 winhttp.dll 方式注入）。
2. 将本压缩包内的 `BepInEx` 文件夹解压到游戏根目录，与已有的 `BepInEx` 合并。
   最终文件应位于：
   - `BepInEx/plugins/LucidCatsChat.dll`
   - `BepInEx/plugins/cnfont.ttf`
3. 启动游戏。

---

## 使用 / Usage

- 进入游戏场景后，按 **回车（Enter）** 唤起聊天框
- 输入文字，按 **回车** 发送
- **Esc** 取消输入
- 消息发送后会自动淡出隐藏

---

## 汉化联动 / Localization

- 若同时安装了汉化补丁（LucidCatsCN），聊天提示会自动显示中文；否则显示英文。
