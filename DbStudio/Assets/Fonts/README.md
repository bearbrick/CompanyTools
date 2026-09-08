# 归档 PDF 字体

`DBStudioSans-Regular.ttf` 和 `DBStudioSans-Bold.ttf` 来自 Noto Sans SC，采用随附的 SIL Open Font License 1.1。字体仅用于服务端 PDF 嵌入，不依赖目标机器安装中文字体。

上游：[notofonts/noto-cjk](https://github.com/notofonts/noto-cjk)，文件 `Sans/Variable/TTF/Subset/NotoSansSC-VF.ttf`；授权原文见 [OFL.txt](OFL.txt)。

生成方式：使用 fontTools 4.64.0 的 `instantiateVariableFont` 将 `wght` 固定为 400 / 700，保留全部原始字符，并将 name 表中的字体家族改名为 DB Studio Sans。未删减 CJK 字符，以支持数据库名称和业务备注中的中文。字体本身继续按 OFL 授权。
