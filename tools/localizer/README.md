# AirFrameXR 中文启动器

这是一个可选的旁置启动器，不改动 AirFrameXR 的 EXE，也不改变官方发布包的目录布局。启动器直接运行 `bin\launcher.exe`，每 250 毫秒扫描同一安装目录中的 AirFrameXR GUI 窗口，并按 `zh-CN.json` 中的文字匹配替换控件文本。除普通窗口文本外，也会处理 Win32 TabControl 标签和 ListView 的列标题、单元格；带 `{0}` 等占位符的键可匹配包含动态值的完整句子。

## 构建和使用

在 Windows 上，从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File tools\localizer\build.ps1
```

将 `out\localizer\ChineseLauncher.exe` 和 `out\localizer\zh-CN.json` 复制到已解压的 AirFrameXR 文件夹根目录（与 `AirFrameXR.exe` 同级），然后运行 `ChineseLauncher.exe`。原有 `AirFrameXR.exe` 仍可照常使用；使用原入口启动时，汉化器不会运行。

## 维护翻译

编辑 `zh-CN.json`：键应与界面控件当前显示的英文完全一致，值为中文译文。动态句子可在键和值中使用 `{0}`、`{1}` 等占位符；匹配时会保留对应的实际内容。保存后重新运行汉化启动器即可生效，不需要重新编译启动器。可以先在界面中记录遗漏的英文，再将完整英文文本作为新键加入 JSON。

## 当前限制

- 这是逐步维护的显示表，不是完整汉化。没有匹配项的文字保留英文；ListView 中动态生成的单元格只有在其完整文字有映射时才会翻译。
- 性能窗口中的自绘图表及其图例、提示文字不通过普通窗口文本 API 绘制，仍可能显示英文；没有可访问窗口句柄的文字、系统菜单以及 ALVR/头显自身界面也不在覆盖范围内。
- 视野配置文件窗口中的自绘视野示意图标签也可能保留英文；配置文件名称由用户自定义，不会被翻译。
- 若目标程序以更高权限运行，Windows 可能阻止汉化器读取或更新其控件文字。
- 窗口切换期间暂时无响应的控件会被跳过并在 500 毫秒后重试；成功替换文字后会合并刷新其父容器，避免旧英文残留或中英文重叠。
- 部分控件有固定宽度，中文译文可能显示不全；可缩短对应译文。
- 汉化器只处理 `bin` 目录中的 `launcher.exe`、`streamer.exe`、`configure.exe`、`performance.exe`、`calibrate.exe` 和 `tray.exe`。
