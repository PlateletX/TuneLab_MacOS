# TuneLab macOS 迁移计划

**当前运行时注记（2026-10-24）：** 为避免 UAD VST3 bundle 在同一进程注册重复 Objective-C 类，最新宿主默认跳过路径名含 `/uaudio_` 的模块；需要专门诊断时可设置 `TUNELAB_VST3_INCLUDE_UNSAFE=1` 恢复扫描。FabFilter 等常规 VST3 效果器仍按默认路径扫描和验证。

**目标：** 在 macOS 上提供可安装、可编辑工程、可播放音频的 TuneLab 版本，并支持 **VST3 效果器**挂载；用户提出的歌声合成目标为 VOCALOID4、VOCALOID5 和 VOCALOID6。三个版本能否在目标 macOS/Apple 芯片环境内直接合成，须逐版验证后才能承诺。首发不包含 VST2、AUv2、AUv3 或乐器插件。

**当前判断：** 迁移可行，核心 UI 和数据模型不需要重写。仓库已包含 Avalonia/.NET 8、macOS 平台分支和 `osx-arm64` CI 构建配置；正式发布仍未开放。首发目标已确定为 **macOS 26 及以上、Apple 芯片 arm64、自包含 ZIP 应用包**；暂不支持 Intel Mac，签名、公证和 DMG 留到后续阶段。

**验证状态（2026-10-24）：** 本机 .NET SDK 8.0.426 已将主程序、CLI 和扩展安装工具以 `osx-arm64` 自包含模式发布，三个 apphost 均为 arm64。当前机器为 macOS 27 Apple 芯片，隔离配置选用 `zh-CN` 后，窗口标题、菜单、轨道和编辑区显示简体中文；headless 截图工具另用真实 Avalonia/Skia 渲染了 26 张界面图。当前源码构建通过，主测试为 717 通过/3 跳过，legacy 兼容测试为 13/13。最低版本配置已切换为 macOS 26.0，并已用重新编译的原生依赖生成新的 Apple Silicon ZIP；物理输出设备播放、设备切换、签名公证和 Gatekeeper 首次打开仍未完成。

**历史实机复测：** 2026-10-23 的自包含包位于 `/private/tmp/TuneLab-macos-verify-20261023/final/TuneLab-osx-arm64-v2.1.1.zip`，当时按 macOS 12 目标生成，`zip -T` 通过并成功验证了工程编辑、VSQX 导入/导出和音频导出。该包的原生库最低版本为 macOS 11.0/12.0，不能代表当前 macOS 26+ 首发门槛；新的 26.0 目标包需要重新编译 LAME、VST3 host 并重新验收。歌声片段为 `Empty Voice`，因此旧复测也不代表 VOCALOID4/5/6 歌声合成可用。

**macOS 26 目标包复测（2026-10-24）：** 最新自包含 ZIP 位于 `/private/tmp/tunelab-macos-validate-current-host-20261024/TuneLab-osx-arm64-v2.1.3.zip`，大小约 132 MiB，SHA-256 为 `69a385585841be2757bcbedd49bb2b84a88e8702451e388c2121900973d5bc01`；`unzip -t` 通过，包内没有重复的 `System.* 2.dll` 或 `TuneLab.Extensions.* 2.dll`。`.app` 的 `CFBundleIdentifier` 为 `app.tunelab.editor`、版本为 `2.1.3`、`LSMinimumSystemVersion` 为 `26.0`；主程序、CLI、扩展安装工具以及随包原生库均为 arm64。LAME 和 VST3 host 的最低系统版本为 macOS 26.0，且 LAME 不再依赖 Homebrew `libmpg123`；自包含 .NET apphost 与 SDL2 保留上游 `minos 11.0`，不会降低应用入口声明的 macOS 26+ 运行门槛。包内 CLI 已读取用户提供的 VSQX，并返回 4 条轨道、576 个音符；当前源码构建的 VST3 host 默认跳过 `/uaudio_` UAD bundle，并只登记效果器类别，FabFilter Pro-Q 4 仍可发现、实例化、读取参数、挂载到 MIDI part、写入自动化并在保存/重开后恢复。该包尚未完成 Developer ID 签名、公证、实体输出设备播放和 VOCALOID4/5/6 实际合成验收。

**VST3 效果器扫描复测：** 2026-10-23 的崩溃报告暴露了旧扫描器会意外加载非效果器插件的问题；这类插件不属于 TuneLab 的支持范围。当前扫描器只把 VST3 效果器类别登记到宿主，并明确排除带有 `Instrument` 子类别的类；VST 乐器和其他非效果器类别不会作为 TuneLab 的挂载选项。2026-10-24 用当前源码重建的 macOS 26 arm64 host 已验证默认过滤逻辑，默认扫描不会加载路径含 `/uaudio_` 的 UAD bundle，也不会登记乐器类；FabFilter 等常规 VST3 效果器仍可发现、实例化、读取参数并参与离线处理。需要专门诊断 UAD 时可设置 `TUNELAB_VST3_INCLUDE_UNSAFE=1` 恢复扫描；UAD 的实际处理仍未列入首发验收。复测 ZIP 位于 `/private/tmp/tunelab-macos-validate-current-host-20261024/TuneLab-osx-arm64-v2.1.3.zip`，大小约 132 MiB，SHA-256 为 `69a385585841be2757bcbedd49bb2b84a88e8702451e388c2121900973d5bc01`；ZIP 完整性通过，VST3 host 为 arm64。该包未经 Developer ID 签名/公证。

**首轮实现：** 已加入自包含 `.app`/ZIP 打包脚本、macOS 文件类型声明、Apple 芯片 CI 与 release 资产上传路径；CLI 和扩展安装工具分别位于 `Contents/Helpers/TuneLab.Cli` 与 `Contents/Helpers/ExtensionInstaller`，SDL2/LAME 及非系统动态依赖随包放入 `Contents/Frameworks`。Mac MP3 编码直接调用随包的原生 LAME，避免 `NAudio.Lame` 在 Mac 上缺失 `LameDLLWrap`；导出前等待音频片段异步加载，避免冷打开工程时首导被截成一秒。桌面窗口与离线音频已在当前 Mac 验证，macOS 26+ 原生依赖包已生成，仍待实体设备和正式分发场景验收。

## 当前基础与缺口

| 领域 | 源码现状 | 迁移影响 |
|---|---|---|
| UI 与运行时 | Avalonia 11、通用 `net8.0` 目标 | 可以沿用现有窗口与编辑器代码 |
| macOS 原生交互 | 已有 CoreGraphics 鼠标处理、⌘主快捷键、钥匙串存储和 macOS 文件打开分支 | 需要实机验证 Retina、多显示器、权限和输入法行为 |
| 构建 | 主程序、CLI 和扩展安装工具的 `osx-arm64` 自包含发布已在本机编译通过；CI 使用 `.app`/ZIP 打包脚本 | 仍需 CI 确认真实依赖打包、产物结构和启动结果 |
| 音频 | 播放通过 ppy.SDL2-CS 随包提供的 SDL2 原生库；Mac MP3 编码直接调用随包的 LAME，其他平台继续使用 NAudio.Lame。设备切换现在先打开目标设备，成功后才关闭当前设备 | WAV/MP3/FLAC/OGG 离线导出已在当前 Mac 验证；SDL dummy 驱动回归测试覆盖切换失败时保留有效设备；实体设备播放/切换仍待实机验收 |
| 音频插件 | 首发仅支持 MIDI/合成 part 效果链上的 VST3 效果器；不支持 VST 乐器、AU 或 VST2。当前包扫描到 46 个效果器，含 FabFilter Pro-Q 4 和 Pro-C 3 | Pro-Q 4 已验证发现、实例化、参数读取、MIDI part 挂载、自动化、保存/重开和离线 WAV/MP3 导出；2026-10-24 修复并测试已保存参数的读取；UADx 仅发现并快速失败后 passthrough |
| 扩展 | manifest 可声明 `osx` 或具体平台 | Windows 原生合成引擎和依赖不会自动获得 macOS 兼容性 |
| 安装与更新 | 已生成实验 ZIP 和 `.app` 文件关联声明；ZIP 内容已核对，包内原生库有 ad hoc 签名，整个应用包签名校验未通过；Developer ID 签名、公证与静默自更新尚未实现 | 仍需从 Finder 安装/双击打开、文件关联和 Gatekeeper 首次启动验收 |
| 发布 | release workflow 已配置 Apple 芯片 ZIP 上传；Linux 上传仍关闭 | 需等 macOS 构建通过后，在 tag 发布中验证资产命名和下载 |

## VST3 插件宿主目标

首发范围已锁定为 **仅 MIDI/合成 part 效果链上的 VST3 效果器**；不支持 VST 乐器、VST2 或 AU，也不扩展到音频 part、master bus。当前音频输出由 SDL2 驱动，`AudioGraph` 混合的是轨道上已经生成的音频源；现有 `EffectGraph` 以离线音频段调用 `IEffectSynthesisEngine`。VST3 原生宿主已编译，托管 `Vst3EffectEngine` 已注册到 `EffectManager`；扫描器只登记 `Fx` 且不含 `Instrument` 子类别的类。FabFilter Pro-Q 4 已验证发现、实例化、参数读取、自动化、旁路、项目保存/重开及离线导出；2026-10-24 修复了数字参数默认值类型不匹配导致已保存参数未传入插件的问题，并加入回归测试。VST3 原生宿主不是独立插件进程，暂不具备插件崩溃隔离。

首发挂载点已确认是 TuneLab 现有的 **MIDI/合成 part 级 Effects 链**，由 `EffectGraph` 调度离线处理；不扩展到音频 part 或 master bus。VST3 宿主实现已完成首个可用闭环：原生依赖、参数读取、MIDI part 挂载、自动化、保存/重开和离线导出均已在 FabFilter Pro-Q 4 上验证；插件自带编辑器窗口和原生进程级崩溃隔离仍未实现。通用 VST3 效果器支持本身也不代表 VOCALOID4–6 有可用的 macOS 歌声插件版本；仍需逐版本实测。

首发通用效果器插件格式定为 VST3，以集中首发实现和验收资源；AUv2/AUv3 暂不纳入发布承诺。macOS 有多种支持 AU 的音频宿主，因此不以“只有 Logic Pro 使用 AU”作为范围依据。Apple 通过系统 SDK 提供 Audio Unit 开发接口；当前暂不支持 AU 是产品范围与排期选择，不是缺少 SDK 所致。此前的 AU 原生 spike 仅用于技术验证，不接入 TuneLab，也不随首发启用。

### 接入边界评估（2026-10-23）

- `IEffectSynthesisEngine` / `IEffectSynthesisSession` 已提供效果器注册、通用参数配置、自动化读取、按片段异步处理及音频输出；`EffectGraph` 已负责串行链、旁路、重处理和失败时 passthrough。可先实现宿主适配器，不改实时 `AudioGraph`。
- `EffectInfo.Properties` 已随工程保存，足以承载插件标识、通用参数和序列化状态字符串；具体状态大小、编码、工程大小和恢复失败策略仍须在真实插件 spike 中验证。插件缺失时现有模型可保留未知类型及参数数据，需确认 UI 的降级呈现。
- VST3 由原生 macOS 桥接库封装，宿主侧效果引擎通过窄 C ABI 调用；DSP 留在 TuneLab 已有的离线会话线程，避免把 VST3 C++ 接口直接暴露给托管层。VST3 宿主和应用接入已完成初步实现；AU 原生 spike 不属于首发范围。
- TuneLab 现有属性面板可用于首个 spike 的通用参数控件。宿主没有插件自带编辑器窗口，也没有 Avalonia 与原生插件视图的嵌入层；首轮先验证参数、状态、自动化和音频处理，是否嵌入原生编辑器留作单独技术门槛。
- **VST3 SDK 准备与效果器验证（2026-10-23 至 24）：** 本机已取得 Steinberg VST3 SDK 3.8.1（tag `v3.8.1_build_84`，commit `3cdf9ca5d1f5b1b21e0a86832aa4abe55607bd96`），并用 Ninja 编译出 hosting 静态库。SDK 根目录含 MIT `LICENSE.txt`，但接口文件头仍指向 Steinberg SDK license/usage terms；在将 SDK 源码或库纳入可分发构建前，仍须按固定版本逐文件核对许可和商标使用规则。本机 VST3 扫描只登记效果器类别，排除标记为乐器用途或其他非效果器用途的类别；扫描到 46 个效果器，包含 FabFilter Pro-Q 4 和 Pro-C 3。Pro-Q 4 已完成实例化、参数读取、MIDI part 挂载、自动化写入、项目保存/重开恢复及有效 WAV/MP3 导出；UADx 仅完成发现并在实例化前快速失败后 passthrough。物理设备播放、插件编辑器窗口和进程级崩溃隔离仍未验收。
- **AUv2 原生 spike（2026-10-23）：** `native/macos/AudioUnitHost` 曾用于验证系统接口和 soothe2 参数/音频处理；它不是 TuneLab 的已接入功能，不纳入首发范围或验收门槛。

### 插件宿主验收要求

- 使用至少一个实际 VST3 效果插件完成扫描、加载、参数修改与自动化、旁路、保存工程、重启后恢复、播放和离线导出。
- 先完成一个最小 VST3 效果器的原生加载/离线处理 spike，验证 macOS 26 部署目标、arm64 架构和可接受的依赖许可证。
- 覆盖插件缺失、架构不符、初始化失败和插件崩溃；单个插件故障不能导致 TuneLab 工程数据损坏或无法打开。
- 插件处理在后台离线合成线程运行；验证插件窗口交互不会阻塞 UI 或破坏合成调度，设备切换不丢失插件实例状态。
- 插件身份、版本、格式、参数、预设/状态和自动化的保存格式要纳入工程兼容测试；随宿主 API 变更遵守冻结 SDK ABI 规则。

## VOCALOID 4–6 目标范围

用户已确认 VOCALOID4、VOCALOID5 和 VOCALOID6 都需要直接合成与工程互通，并以 VOCALOID6 桌面版作为当前使用环境。这里需要分别验收两种能力，不能互相替代：

| 版本 | 可能涉及的工程格式 | 仓库现状 | 需要验证的合成能力 |
|---|---|---|---|
| VOCALOID4 | VSQX | 已实现内建 VSQX 导入和导出；用户提供的 V4 样本已在 Mac 导入、导出并重新导入，模型级结构保持一致。尚未在 VOCALOID4 应用内打开导出文件；V3 样本也未验证 | 是否存在可在目标 macOS 版本和 arm64 上由 TuneLab 调用的授权引擎/桥接接口 |
| VOCALOID5 | VPR | 已加入 VPR 导入和导出，并以合成 JSON/ZIP 样本验证基础轨道、音符、控制器、V5 声源标识及未知字段保留；没有真实 VPR 样本，尚未验证实际文件字段、数据映射或损失 | 按实际 VOCALOID5 版本验证可调用接口、授权、系统版本与原生依赖 |
| VOCALOID6 | VPR | 与 VOCALOID5 共用基础 VPR 导入/导出实现；只有合成 V6 样本测试，没有真实工程验证 | 官方当前规格要求 macOS 14 或更高版本；macOS 26+ 首发目标满足该系统版本门槛，但仍需分别验证 V6 引擎接入、授权和实际合成，不得把工程互通当成直接合成已完成 |

**工程文件互通不等于歌声合成。** 用户要求逐版本支持 VSQX/VPR 工程导入、编辑和导出；当前 VSQX 与 VPR 都有导出实现，但真实 VOCALOID 工程字段映射仍需样本验收。格式映射验证也不能替代 TuneLab 调用获授权引擎并完成渲染。通用 VST3 宿主当前仅按效果器需求实现，不支持乐器插件；VOCALOID6 的运行方式及授权/渲染接口仍需单独验证，不能据此推定其可由当前效果器宿主驱动。VPR 是私有且随版本演进的格式，未取得真实样本前不承诺完整兼容，也不以合成测试样本代表真实工程验收。

**官方运行时边界（2026-10-24 核对）：** VOCALOID6 官方支持 macOS 14、15 和 26，以及 Apple Silicon；其编辑器可 standalone 运行，也提供 VST3/AU 形态，并读写 VPR、VSQX 和 MIDI。VOCALOID5 在 Apple Silicon 上要求 5.6.3 或更高版本。VOCALOID4 Editor 的官方系统要求仍是 Windows，因此本项目在 macOS 首发中把 V4 定义为 VSQX 工程互通目标；V4 声库能否由 V5/6 的 Mac 运行时合法加载，仍须用户授权状态和实际声库逐一验证。当前 TuneLab 的 VST3 首发范围只包含效果器，不能把它当作 VOCALOID VSTi 宿主。

在外部调用接口、官方平台支持和许可条件未核实前，VOCALOID4–6 是目标要求，**不是当前已有或已验证的 macOS 功能**。任何一个版本若没有 macOS arm64 可用的调用路径，应如实标成不支持/待外部桥接，不以 ZIP 文件兼容或 Windows 虚拟机代替原生支持。

### 已收到的 VOCALOID4 样本

2026-10-22 收到一份 VSQX 4.0.0.3 实际工程，原文件留在用户下载目录，不纳入仓库。SHA-256 为 `87f52675468f0642aa83643b370823cb4b67fcae60c012d46d2f23961c805247`，XML 结构检查通过。它使用 480 PPQ、4 小节预备拍和 94.01 BPM；包含 2 条歌声轨、3 个歌声片段、576 个音符（两轨分别为 525/51）、375 个片段级控制点（D 365、P 5、S 5），另有音符样式控制和 2 个 WAV 片段。声源标识为 YuezhenglingV3，文件中还含 VOCALOID 插件状态。

两个 WAV 片段指向原作者的 Windows 绝对路径；未取得实际音频前，不能验收伴奏/参考音频的播放。内建 VSQX 导入器已读取音符、歌词、发音、片段位置、速度、拍号、D/P/S 控制点及 WAV 引用；样本的轨道/片段/音符数经包内 CLI 核对，GUI 显示缺失音频的重新关联提示，保存为 `.tlpx` 后可重开。当前 VSQX 导出器将 TuneLab 内容写为 VSQ4，并在真实样本上完成 CLI 导入→导出→再导入，轨道、音符数和时间设置保持一致；自动化及样式/歌手 XML 元数据有模型级往返测试。导出文件尚未在 VOCALOID4 应用内打开验证，不能据此承诺无损或完整互通。歌声片段目前显示 `Empty Voice`，无法用 VOCALOID 声源合成；声源和插件状态也尚未在实际合成环境中验证。

## 阶段 0：确定首发范围并建立基线

### 工作项

- 首发基线为 macOS 26 及以上、Apple 芯片 arm64。若后续支持 Intel Mac，新增 `osx-x64` 构建并单独验证 SDL2、LAME 和合成引擎的 x64 原生依赖。
- 首发 ZIP 内置 .NET 运行时，用户无需预先安装 .NET；签名、公证和 DMG 作为后续分发里程碑。
- 记录可用于验收的项目文件、MIDI、音频文件和至少一个候选 macOS 合成引擎/插件。
- 在实际 Mac 上确认 .NET 8 SDK 可用，并发布主程序、CLI 与扩展安装工具：

```sh
dotnet publish TuneLab/TuneLab.csproj -c Release -r osx-arm64 --self-contained true
dotnet publish TuneLab.Cli/TuneLab.Cli.csproj -c Release -r osx-arm64 --self-contained true
dotnet publish ExtensionInstaller/ExtensionInstaller.csproj -c Release -r osx-arm64 --self-contained true
```

### 验收标准

- 主程序、CLI 和扩展安装工具已在本机成功发布；CI 仍需确认干净 checkout 上发布及资源复制结果。
- 清楚记录最低 macOS 版本、CPU 架构、运行时分发策略和首发合成引擎。

**退出条件：** 若构建失败，先修复平台编译、MSBuild RID 和资源复制问题，再进入音频与 UI 验证。

## 阶段 1：验证原生依赖与核心音频

### 工作项

- macOS 包将发布目录中的 SDL2 与 LAME 的非系统动态依赖放入 `.app/Contents/Frameworks`，并为 SDL2 绑定、主程序和包内 CLI 注册定向 resolver；CI 将 LAME 和 mpg123 按 macOS 26 部署目标从源码构建，并检查库文件架构、最低系统版本及依赖路径。
- 在构建出的 ZIP 中确认库随包存在；随后在 macOS 26+ Apple 芯片实机验证 SDL2 与 LAME 实际加载。Homebrew 预编译库不能直接用于首发包，因为其最低系统版本取决于制包系统。
- 在物理音频输出设备上验证自动选择、指定设备、设备切换、播放、停止、暂停、设备拔插和无设备启动。
- 覆盖 WAV、MP3、FLAC、OGG 的导出与解码；确认错误时用户能得到可诊断的信息，不会导致整个应用退出。

### 验收标准

- Mac 首次启动不依赖系统预装 SDL2/LAME。
- 音频播放与设备切换稳定；所有首发承诺的格式都有读写测试结果。未通过的格式从首发承诺中移除，或在发布前修复。

**退出条件：** SDL2 或 LAME 的加载问题未解决时，不进入发布打包阶段。

## 阶段 2：验证编辑器与 macOS 交互

### 工作项

- 在 Retina 屏幕和多显示器环境测试鼠标拖动、光标隐藏/恢复、钢琴卷帘与自动化曲线编辑；重点观察 CoreGraphics 坐标与 Avalonia 缩放之间的偏差。
- 验证 ⌘C/⌘V/⌘Z/⌘S 等常用组合键、快捷键设置界面、文本输入和中文/日文输入法组合输入。
- 验证 Avalonia 文件选择器、拖入工程文件、启动参数打开文件、保存与另存为。
- 验证应用数据目录、日志、自动保存、最近文件、钥匙串密钥读写；检查应用在钥匙串提示或凭据不可用时的行为。
- 比较 Windows 与 macOS 对 `.tlp`、`.tlpx` 工程的打开、保存和重新打开结果，确保文件格式与工程数据兼容。

### 验收标准

- 可新建、编辑、保存、关闭并重新打开工程；常见编辑手势无平台特有故障。
- 可导入 MIDI、播放工程、编辑歌词/音符/参数并导出音频。
- 中文输入、常用快捷键和文件操作通过人工验收。

## 阶段 3：验证 VOCALOID 4–6 接入路径

### 工作项

- 按 VOCALOID4、VOCALOID5、VOCALOID6 分别验证可调用方式、macOS 版本、Apple 芯片架构、授权流程、离线渲染/实时合成能力及原生依赖。用户当前提供的环境信息是 VOCALOID6 桌面版；V4、V5 仍需实际测试环境或可复现的授权测试方式。
- 先确认 TuneLab 可调用的是官方 SDK/API、用户授权的独立桥接程序，还是需要新增 VST3 插件宿主。通用 VST3 宿主已在 Apple 芯片 Mac 编译，并接入 TuneLab 的 MIDI/合成 part 效果链；FabFilter Pro-Q 4 已完成真实插件扫描、实例化、参数读取、自动化、保存/重开和离线音频处理验收。这只证明效果器宿主链路，不代表可直接承载 VOCALOID。若以插件宿主接入 VOCALOID，必须把格式、隔离、安全与 Apple Silicon 兼容性作为独立工程评估。AU 不属于通用效果器首发范围。
- 首发宿主范围锁定为 macOS **VST3 效果器**；不包含 VST2、AUv2、AUv3、VST 乐器、音频 part 效果槽或 master bus 效果槽。挂载点沿用 MIDI/合成 part 级 Effects 链。
- 评估宿主实现前检查依赖许可证、Apple 芯片支持、macOS 26 部署目标、插件参数/编辑器 UI、状态与预设持久化、自动化和崩溃隔离。
- 用 VST3 样例效果器验证原生加载、离线音频处理、参数读写、插件状态保存/恢复和 Apple 芯片构建，再接入 TuneLab 的效果面板与工程格式。2026-10-23 本机原生冒烟扫描到 50 个效果器类，FabFilter Pro-Q 4 成功实例化、读取 737 个参数并处理 256 帧立体声音频；托管引擎现已注册到 TuneLab 效果系统，最新过滤版扫描到 46 个效果器（含 Pro-Q 4、Pro-C 3，且不含 `Fx|Instrument` 类）。含 Pro-Q 4 的隔离工程已完成合成与非静音 WAV 导出；后续复测又完成了参数变化、自动化、旁路、项目保存/重开恢复以及 WAV/MP3 导出。物理设备播放、插件自带编辑器窗口和进程级崩溃隔离仍未验收。
- 同日收到的 `vst3host-smoke` 崩溃报告发生于 19:32:13，调用栈显示 UAD Lexicon 224 的 `bundleExit` 在 VST3 模块卸载时释放无效指针。当前扫描器只保留插件元数据，创建效果器时按路径加载目标 bundle，避免把所有第三方模块长期挂在进程内；UAD 重复 Objective-C 类警告仍由插件自身的全局类名造成，不能通过普通 VST3 生命周期管理消除。
- 旧版扫描器曾在发现效果器时加载非效果器模块，导致 CLI 退出崩溃。当前扫描结果只登记 VST3 `Fx` 类，排除 `Instrument` 子类别；非效果器插件不属于支持或验收目标。FabFilter Pro-Q 4 已完成发现、实例化、参数读取和离线处理；UAD 目前只完成发现，实例化会快速失败并降级，避免第三方授权/UI 初始化阻塞主程序。
- 已将 VST3 效果器接入现有离线 effect 调度；FabFilter Pro-Q 4 已核对宿主/处理线程生命周期、参数状态恢复、自动化、旁路降级和 WAV/MP3 导出。插件编辑器窗口、进程级崩溃隔离及实体设备播放仍待验收。
- 以至少一个真实 VST3 效果插件验证扫描、加载、编辑参数、自动化、旁路、保存/恢复和离线导出；FabFilter Pro-Q 4 已完成扫描、实例化、参数读取、MIDI part 挂载、自动化写入、项目保存/重开恢复及 WAV/MP3 导出验收，且导出的 WAV/MP3 文件有效。2026-10-24 复查发现宿主以 Null 作为数字参数读取的默认值，触发属性类型回退，导致工程中已保存的参数没有送入插件；现已改用数字默认值，并以隔离工程重渲染确认处理后的 WAV 与旁路/基线哈希不同，增加了参数读取回归测试。物理设备播放、插件自带编辑器窗口和进程级崩溃隔离仍属于后续门槛；UADx 只完成发现与 passthrough 降级验证。测试矩阵覆盖目标架构与插件失败模式。
- 将工程互通作为单独能力评估：VOCALOID4 的 VSQX，以及 VOCALOID5/6 的 VPR。收集各版本真实工程样本，分别验证导入、编辑后导出、参数映射和往返损失；真实 Vocaloid 应用打开导出文件的验收仍未完成。
- 内建 VPR 已实现基础导入和导出：处理 Project/sequence.json 中的轨道、片段、音符、常用控制器、节拍/拍号和基础轨道电平；导入时在片段/音符属性中保留原始 JSON，并在导出编辑后的内容时尽量保留未知字段。当前只有合成 JSON 样本的导入、编辑、导出、再导入测试；需要真实 VOCALOID5/6 .vpr 验证实际文件结构、字段单位、往返损失，并用对应 Vocaloid 应用尝试打开导出文件。
- 截至 2026-10-23，[VOCALOID6 官方规格](https://www.vocaloid.com/vocaloid6/specs/)列出的最低 macOS 为 14，并注明支持 Apple Silicon、独立运行、VST3 和 AU；TuneLab 首发自身目标为 macOS 26+，系统版本交集不再是主要限制，但 V6 合成仍需按授权、引擎接口和真实工程逐项验收。
- 为候选插件检查 manifest 的 `platforms`、目标架构、目标框架以及所有原生依赖；把纯托管插件与依赖 Windows DLL/EXE 的插件分别记录。
- 在 macOS 上验证包扫描、加载、启停、路由、设置、卸载和错误状态显示。
- 对 V4、V5、V6 各自完成从 TuneLab 音符编辑到目标引擎渲染、播放和导出的完整链路；无法原生接入的版本单独列明限制和后续方案。
- 为不兼容的 Windows 插件给出清晰的“平台不支持”状态，确认单个插件失败不会阻止其他插件和主程序启动。

### 验收标准

- VOCALOID4、VOCALOID5、VOCALOID6 分别有真实环境的合成验收记录，并明确各自最低 macOS 版本；若任何版本尚未通过，发布说明必须逐版披露，不得笼统声称支持 VOCALOID4–6。
- 工程导入/导出与引擎合成分别记录结果；VPR 导入、VPR 导出与引擎合成是三项独立能力，一个能力通过不得替代其他能力的验收。
- 每个随首发宣传的引擎/插件均有明确的平台、架构、版本与授权要求记录。

**退出条件：** 没有可用合成引擎时，可以继续内部编辑器验证，但不宣布 macOS 版本具备完整歌声合成功能。

## 阶段 4：Mac 应用集成、安装和更新

### 工作项

- 生成标准 `.app` bundle，补齐 `Info.plist`、应用图标和启动元数据；GUI 放入 `Contents/MacOS`，CLI 与扩展安装工具分别放入 `Contents/Helpers` 下的独立自包含目录，并确定运行时、资源和原生 dylib 的布局。
- 为 `.tlp`、`.tlpx` 和 `.tlx` 配置 Finder 文件类型关联，并验证双击打开工程和安装扩展的启动参数流程。
- 首发使用包含自包含 `.app` 的 ZIP，用户可拖入“应用程序”文件夹；后续再选定 `.dmg` 或 `.pkg` 并设计用户级升级和卸载流程，不复用 Windows 注册表安装逻辑。
- 后续配置 Developer ID 代码签名和 Apple notarization；在未安装开发工具的干净 Mac 上验证 Gatekeeper 首次启动体验。
- 首发可以沿用“打开下载页、用户手动安装”的更新体验。若要求应用内自更新，再单独实现与 `.app` 和已签名包匹配的更新流程。

### 验收标准

- ZIP 可在 macOS 26 及以上的 Apple 芯片 Mac 上展开并启动；在完成签名和公证前，首次启动按实验包说明由用户在 Finder 确认打开。
- Finder 文件关联工作，包内所有托管资源和原生库都随包发布。
- 更新策略与实际发布资产一致，不会尝试启动 Windows `.exe` 安装器。

## 阶段 5：CI、发布和维护

### 工作项

- 在 `build-artifacts` workflow 中维持显式 macOS 打包路径，分别发布主程序、CLI 和扩展安装工具；不要依赖从解决方案根目录发布时的隐式项目选择。
- macOS runner 运行主测试时排除 Windows 安装器专属用例，并运行 legacy compatibility 测试；2026-10-24 本机主测试为 717 通过/3 跳过，legacy compatibility 测试 13/13 通过。Windows runner 运行未过滤的完整测试集，以覆盖被 Mac 排除的安装器用例；远端 workflow 首次通过后再确认 CI 门槛。
- 已为 Apple 芯片 ZIP 配置 GitHub release 上传步骤；待 CI 通过后验证资产名称与更新服务识别的 `osx-arm64` 平台名称一致。
- 编写简体中文 Mac 安装、首次启动、数据目录、扩展兼容和排错说明；README 明确支持的芯片、最低系统版本和已验证的合成引擎。
- 先发布给小范围测试用户，收集启动、音频设备、工程文件和插件问题，再公开发布。

### 验收标准

- 从 tag 到实验 ZIP 资产上传的流程可重复运行，发布失败会阻止资产上传；后续签名/公证链路也须独立验收。
- 干净环境安装测试、核心编辑与音频验收、插件验收均通过后，才开放正式下载。

## 主要风险与处理顺序

| 优先级 | 风险 | 处理方式 |
|---|---|---|
| P0 | SDL2 或 LAME 原生库无法在目标 Mac 加载 | CI 将随包打入对应 dylib 并检查架构、依赖与最低系统版本；仍须实机播放和导出验收 |
| P0 | VOCALOID4/5/6 没有可由 TuneLab 在 macOS arm64 调用的授权接口 | 阶段 3 按版本调查官方接口/桥接方式；没有可行路径的版本不承诺支持 |
| P0 | VOCALOID 工程导入格式与引擎合成被混为一谈 | 按 VSQX/VPR 与直接合成分开实现、测试和宣传 |
| P0 | VST3 效果器宿主无法满足最低系统版本、Apple 芯片或目标插件的线程/授权要求 | 先用真实目标效果插件完成技术验证，再锁定宿主实现和隔离边界 |
| P0 | 插件参数与运行状态无法稳定写入 TuneLab 工程 | 先定义带版本号的插件实例/参数/状态存储结构，再覆盖保存、重开和缺失插件降级 |
| P1 | Retina、多显示器下鼠标坐标或隐藏状态异常 | 阶段 2 在实际硬件上覆盖缩放和多屏场景 |
| P1 | 未签名应用被 Gatekeeper 阻止 | 阶段 4 配置 Developer ID 签名与 notarization，并在干净设备验收 |
| P1 | macOS 文件关联与安装/更新仍沿用 Windows 假设 | 使用 `.app` 元数据和独立 Mac 安装/更新流程 |
| P2 | Intel Mac 未覆盖 | 阶段 0 明确产品范围；若承诺支持，再增加 `osx-x64` 与原生依赖测试 |

## 建议的首发门槛

首发只承诺已经验证的 macOS 版本、CPU 架构、音频格式和插件。实验 ZIP 阶段的最低门槛是：macOS 26 及以上的 Apple 芯片 Mac 能解压并启动自包含 `.app`；工程可编辑并兼容现有文件格式；音频播放和首发格式导出可靠；VST3 效果器通过扫描、编辑、自动化、保存/恢复、播放和导出验收；AU 插件明确不在首发支持范围内；VOCALOID4、VOCALOID5、VOCALOID6 的合成支持逐版完成验收，或在发布说明中清楚列出未支持版本；VSQX/VPR 工程互通另行验收；不兼容扩展能被清晰识别并隔离。完成签名、公证和 DMG 后，再按正式分发标准发布。
