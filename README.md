# legend-of-mortal-uncap-plus

《活侠传》(Legend of Mortal) 三合一 Mod:**六维/武功上限解除 · 更好的天命点分配 · 肌肉立绘联动**。
基于 BepInEx + Harmony,运行时注入,不修改游戏任何文件;自带游戏内设置面板,所有功能独立开关。

A 3-in-1 mod for *Legend of Mortal* (活侠传): **uncap six stats & martial arts**,
**a much better fate-point allocation UI**, and **muscle portrait swap**.
Built on BepInEx + Harmony — no game files are modified. Ships with an in-game
config panel; every feature has its own switch.

---

## 功能一览

### 1. 上限解除(UncapSixStats)

- 六维(體力/內力/輕功/拳掌/刀劍/暗器)和全部武功类属性取消 100 上限;
- 属性面板雷达图新增"破限层":原六边形保持原版观感,超出部分以亮银荧光向外流动;
- 曲线类加成(如血量)破限后按曲线尾部斜率继续增长,努力不白费(可关);
- **不是改大 Max**,只放开数值钳制:90 点永远等于 90%,绝无"稀释变弱"。

### 2. 更好的天命点分配(v2.0.0)

通关/新结局/新死法攒下的天命点,开新周目时的继承加点全面重做:

- **加点立即落实**,属性/人际/门派三页随便切换,不再有"未确认点数"拦截;
- **每项都有减号**,只退你刚分配的点,初始值减不动;没分配过时减号不出现;
- **性格按偏离初始值计费**:远离中立扣点、点回中立退点,来回 0 消耗;
- **复原 = 回到最初**(三页全退,点数全返还);**确定 = 结束加点**;
- 剩点没用完时弹窗提示"将转为命运点",可勾「之後不再提示」;
- 六维/武學點數/銀兩/鍛造/煉丹加点可破原上限,**上限内蓝色、破限金色**;
- **长按 + / − 连点**;
- 点数严格守恒,`BepInEx/LogOutput.log` 留有 `[天命]` 审计行可对账。

### 3. 肌肉立绘联动

任一六维破 100,主角立绘当场切换为肌肉版(剧情对话/庭院常驻/状态页头像全覆盖),
掉回 100 以下自动还原;素材来自韩国玩家的 giga_player 立绘包。

### 其他贴心小修

- 银两超过 99999 后各处钱数自动缩字,六位、七位完整显示;
- 面板 UI:× 关闭、滑条开关,繁体文案贴合游戏字体。

## 安装

1. 到 [Releases](../../releases) 下载最新 `UncapSixStats_v*_一键安装包.zip`;
2. 解压,把压缩包内的所有内容(`BepInEx/`、`winhttp.dll`、`doorstop_config.ini`、`changelog.txt`)
   复制到游戏根目录(即 `Mortal.exe` 所在目录,Steam 一般是
   `steamapps/common/LegendOfMortal`);
3. 启动游戏。**读档或开新游戏进入游戏世界后**,右上角齿轮「系統」菜单里会多出
   「上限解除」按钮(按 **F2** 也可开关设置面板)。

不想用某个功能:面板里关掉对应滑条即可;肌肉立绘素材不想要可整目录删除
`BepInEx/plugins/GigaMuscle/`。

卸载:删除游戏根目录的 `BepInEx/`、`winhttp.dll`、`doorstop_config.ini`、`changelog.txt`。
卸载后读档会把超过 100 的数值重新钳回 100,不会炸档。

## 热键

| 键 | 作用 |
|---|---|
| F2 | 开关设置面板 |
| F10 | 打印数值转换诊断到日志(只读) |
| F9 | 武功拳掌 +10(默认关闭,真实加点,慎用) |

## 从源码构建

```
cd plugin
dotnet build -c Release
# 产物 bin/Release/UncapSixStats.dll,复制到游戏 BepInEx/plugins/
```

需要 .NET SDK 8+(工程目标框架 netstandard2.0,引用直接指向本机游戏目录的 DLL)。
实现原理、补丁明细(Harmony patch 一览)、踩坑记录见 [README_DEV.md](README_DEV.md)。

## 致谢

- 肌肉立绘素材:韩国玩家 **giga_player** 立绘包(素材版权归原作者所有;
  若作者要求下架请开 Issue,随包分发仅为方便玩家);
- Mod 框架:[BepInEx](https://github.com/BepInEx/BepInEx)(LGPL-2.1)、Harmony;
- 《活侠传》开发组 Obb Studio。

## 许可证

- 本仓库**源代码**以 [MIT](LICENSE) 发布;
- `GigaMuscle/` 立绘素材版权归原作者所有,**不随 MIT 授权**,二次分发请自行取得原作者许可;
- 游戏本体及其素材版权归 Obb Studio 所有;本仓库不含任何游戏文件与反编译代码。

---

## For English speakers

**Features**

- **Uncap**: removes the 100-point cap on the six core stats and all martial-arts
  stats. The radar chart keeps its vanilla look and gains a subtle silver
  "over-cap" glow; curve-based conversions (e.g. HP) keep growing past 100.
- **Better fate-point allocation** (the NG+ inheritance screen): instant apply,
  minus buttons everywhere, fair personality pricing (pay only for distance
  from your starting value), free tab switching, true "reset all", a
  leftover-points reminder with a "don't show again" checkbox, over-cap values
  allowed (blue = within the original cap, gold = beyond), and press-and-hold
  to spam clicks. Every fate point is conserved and auditable in the log.
- **Muscle portrait swap**: break 100 in any core stat and the protagonist's
  portrait swaps to the muscle version everywhere; drop back below 100 and it
  reverts. Art from the Korean giga_player portrait pack.

**Install**: download the latest zip from Releases, extract into the game folder
(next to `Mortal.exe`), run the game, load a save, then open the gear ("系統")
menu and click 「上限解除」 (or press F2).

**Build from source**: `cd plugin && dotnet build -c Release`, then copy
`bin/Release/UncapSixStats.dll` into the game's `BepInEx/plugins/`.

**License**: source code is MIT; GigaMuscle art assets belong to their original
author and are not MIT-licensed; the game itself belongs to Obb Studio.
