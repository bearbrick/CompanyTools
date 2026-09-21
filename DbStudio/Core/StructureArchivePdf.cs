using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace DbStudio.Core;

/// <summary>服务器独立生成可检索的横版中文 PDF；横版封面、目录与紧凑正文共用分页、换行规则。</summary>
public sealed class StructureArchivePdf
{
    private static readonly SemaphoreSlim RenderSlots = new(2);

    static StructureArchivePdf()
    {
        GlobalFontSettings.FontResolver = new ArchiveFontResolver();
    }

    /// <summary>限制同时排版数量，等待及长文档生成均响应取消，不占用 Blazor 数据传输通道。</summary>
    public async Task<byte[]> GenerateAsync(StructureArchive archive, CancellationToken cancellationToken = default)
    {
        archive.Options.Validate();
        await RenderSlots.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                using var layout = new Layout(archive, cancellationToken);
                return layout.Render();
            }, cancellationToken);
        }
        finally
        {
            RenderSlots.Release();
        }
    }

    private sealed class ArchiveFontResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            familyName == "DB Studio Sans" ? new FontResolverInfo(bold ? "Bold" : "Regular") : null;

        public byte[]? GetFont(string faceName)
        {
            using var source = typeof(StructureArchivePdf).Assembly.GetManifestResourceStream($"DbStudio.Assets.Fonts.DBStudioSans-{faceName}.ttf")
                ?? throw new InvalidOperationException("归档字体缺失，请重新部署完整应用。");
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private sealed class Layout : IDisposable
    {
        private const double Left = 36;
        private const double Width = 770;
        private const double Top = 43;
        private const double Bottom = 535;
        private static readonly XColor Ink = XColor.FromArgb(37, 56, 74);
        private static readonly XColor Navy = XColor.FromArgb(22, 50, 75);
        private static readonly XColor Muted = XColor.FromArgb(94, 111, 124);
        private static readonly XColor Pale = XColor.FromArgb(243, 247, 249);
        private static readonly XColor Heading = XColor.FromArgb(229, 239, 244);
        private static readonly XPen Border = new(XColor.FromArgb(217, 227, 233), 0.4);
        private static readonly double[] ColumnWidths = [100, 95, 90, 48, 110, 42, 42, Width - 527];
        private static readonly double[] DirectoryColumnWidths = [38, 150, 225, 260, Width - 673];
        private static readonly HashSet<int> CenteredKeyColumns = [5, 6];
        private readonly StructureArchive archive;
        private readonly CancellationToken cancellationToken;
        private readonly PdfDocument document = new();
        private readonly Dictionary<(double, bool), XFont> fonts = [];
        private readonly Dictionary<(string, double, bool), double> measures = [];
        private readonly Dictionary<string, int> tablePages = [];
        private readonly List<(PdfPage Page, double Y, string TableId)> directoryRows = [];
        private PdfPage page = null!;
        private XGraphics graphics = null!;
        private double y;

        internal Layout(StructureArchive archive, CancellationToken cancellationToken)
        {
            this.archive = archive;
            this.cancellationToken = cancellationToken;
        }

        internal byte[] Render()
        {
            document.Info.Title = archive.Project.Name + " · 数据库结构归档说明书";
            document.Info.Author = archive.Options.PreparedBy;
            document.Info.Subject = $"已保存项目设计 r{archive.Project.Revision}；{archive.Options.Version}";
            document.Info.Creator = "DB Studio";
            Cover();
            Directory();
            NewPage();
            document.Outlines.Add("表结构明细", page, true);
            Paragraph("表结构明细", 12, true);
            Paragraph("空值限制留空表示允许 NULL；默认值“无”表示无显式默认值。计算列类型与空值性由数据库表达式推导。", 8, color: Muted);
            if (!string.IsNullOrWhiteSpace(archive.Project.Description))
            {
                Paragraph("项目说明：" + archive.Project.Description, 8, color: Muted);
            }
            y += 8;
            var number = 0;
            foreach (var table in StructureArchiveText.Tables(archive.Project))
            {
                DrawTable(table, ++number);
            }
            graphics.Dispose();
            graphics = null!;
            FillDirectoryPageNumbers();
            for (var i = 0; i < document.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var footer = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append);
                footer.DrawLine(Border, Left, 553, Left + Width, 553);
                footer.DrawString($"DB Studio  |  {archive.Options.Version}  |  项目修订 r{archive.Project.Revision}", Font(8), new XSolidBrush(Muted), new XPoint(Left, 570));
                footer.DrawString($"{i + 1} / {document.PageCount}", Font(8), new XSolidBrush(Muted), new XRect(Left, 558, Width, 16), XStringFormats.CenterRight);
            }
            using var output = new MemoryStream();
            document.Save(output, false);
            return output.ToArray();
        }

        private void Cover()
        {
            NewPage();
            document.Outlines.Add("归档说明", page, true);
            y += 6;
            Paragraph("数据库结构归档说明书", 21, true);
            Paragraph(archive.Project.Name, 11.5, true);
            y += 6;
            CompactSection("一、文档信息");
            FormalTable(["项目", "内容", "项目", "内容"], [82, 298, 82, Width - 462],
            [
                ["文档编号 / 版本", $"{Filled(archive.Options.DocumentNumber)} / {archive.Options.Version}",
                    "文档状态 / 密级", $"已生成 / {Filled(archive.Options.Classification)}"],
                ["数据库 / 产品版本", $"{Filled(archive.Options.DatabaseName)} / SQL Server {archive.Options.DatabaseVersion}",
                    "来源环境 / 设计基线", $"{Filled(archive.Options.Environment)} / 已保存设计 r{archive.Project.Revision}"],
                ["导出时间", archive.ExportedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                    "编制部门 / 编制人", $"{Filled(archive.Options.Department)} / {Filled(archive.Options.PreparedBy)}"]
            ], size: 8.3, padding: 4.5, bottomGap: 4);
            CompactSection("二、归档范围与依据");
            FormalTable(["项目", "内容"], [132, Width - 132],
            [
                ["归档范围", $"{archive.Project.Tables.Count} 张表、{archive.Project.Tables.Sum(t => t.Columns.Count)} 个字段；包含主键、索引、外键、检查约束和设计说明。"],
                ["结构来源", "DB Studio 已保存的项目设计。本次未连接实际数据库；设计与实际环境的一致性留待核验。"],
                ["范围说明", "不含业务数据、账号和连接配置；视图、过程、函数等未纳入设计器的对象不在本文件归档范围。"]
            ], size: 8.3, padding: 4.5, bottomGap: 4);
            CompactSection("三、阅读说明");
            Paragraph("全文采用 A4 横版。正文按模块连续排列，字段名后紧邻中文定义名，主键与外键以实心圆在对应字段行标识，索引单独列于字段表下；长表续页重复表名和表头，可通过 PDF 书签定位数据表。", 8.3, padding: 3);
        }

        /// <summary>生成可见目录；页码在正文排版完成后回填，书签继续用于电子导航。</summary>
        private void Directory()
        {
            NewPage();
            document.Outlines.Add("目录", page, true);
            Paragraph("目录", 23, true);
            Paragraph("物理表名与中文业务名成对列示；正文仍按模块顺序排列。", 9.3, color: Muted);
            y += 10;
            void Header() => Row(["序号", "模块 / 分类", "物理表名", "中文业务名", "页码"], DirectoryColumnWidths, 8.2, true, Heading, padding: 5);
            Header();
            var number = 0;
            foreach (var table in StructureArchiveText.Tables(archive.Project))
            {
                Ensure(34, Header);
                var rowPage = page;
                var rowY = y;
                Row([
                    (++number).ToString("00"),
                    Shorten($"{table.Module} / {TableDataCategories.Get(table.DataCategory).Name}", 34),
                    Shorten($"{table.Schema}.{table.Name}", 48),
                    Shorten(string.IsNullOrWhiteSpace(table.Label) ? "（未填写中文名）" : table.Label.Trim(), 48),
                    ""
                ], DirectoryColumnWidths, 8.2, background: number % 2 == 0 ? Pale : null, repeat: Header, padding: 5);
                if (ReferenceEquals(rowPage, page))
                {
                    directoryRows.Add((rowPage, rowY, table.Id));
                }
            }
        }

        private void FillDirectoryPageNumbers()
        {
            foreach (var group in directoryRows.GroupBy(row => row.Page))
            {
                using var overlay = XGraphics.FromPdfPage(group.Key, XGraphicsPdfPageOptions.Append);
                foreach (var row in group)
                {
                    if (tablePages.TryGetValue(row.TableId, out var targetPage))
                    {
                        overlay.DrawString(targetPage.ToString(CultureInfo.InvariantCulture), Font(8.2), new XSolidBrush(Ink),
                            new XRect(Left + DirectoryColumnWidths.Take(4).Sum() + 6, row.Y + 5,
                                DirectoryColumnWidths[^1] - 12, 13), XStringFormats.TopLeft);
                    }
                }
            }
        }

        private void DrawTable(TableDesign table, int number)
        {
            cancellationToken.ThrowIfCancellationRequested();
            void Header(bool continued)
            {
                Paragraph($"{number:00}  {StructureArchiveText.TableTitle(table)}{(continued ? "（续）" : "")}", 10, true, background: Heading);
                Row(["字段名", "中文名", "类型", "空值", "默认 / 生成", "主键", "外键", "说明"], ColumnWidths, 8, true, Pale, padding: 3, centeredCells: CenteredKeyColumns);
            }
            void Repeat() => Header(true);
            Ensure(105);
            tablePages[table.Id] = document.PageCount;
            document.Outlines.Add($"{number:00} {StructureArchiveText.TableTitle(table)}", page, false);
            Paragraph($"{number:00}  {StructureArchiveText.TableTitle(table)}", 10, true, background: Heading);
            Paragraph($"模块：{table.Module}  |  数据分类：{TableDataCategories.Get(table.DataCategory).Name}", 8, color: Muted, repeat: Repeat);
            if (!string.IsNullOrWhiteSpace(table.Comment))
            {
                Paragraph("备注：" + table.Comment, 8, color: Muted, repeat: Repeat);
            }
            if (y + 40 > Bottom)
            {
                NewPage();
                Paragraph($"{number:00}  {StructureArchiveText.TableTitle(table)}（续）", 10, true, background: Heading);
            }
            Row(["字段名", "中文名", "类型", "空值", "默认 / 生成", "主键", "外键", "说明"], ColumnWidths, 8, true, Pale, padding: 3, centeredCells: CenteredKeyColumns);
            foreach (var column in table.Columns)
            {
                var generation = StructureArchiveText.Generation(column);
                if (column.Default != "" && column.DefaultConstraintName != "")
                {
                    generation += $"\n约束：{column.DefaultConstraintName}{(column.DefaultConstraintSystemNamed ? "（系统命名）" : "")}";
                }
                var description = StructureArchiveText.Description(column);
                if (column.Collation != "")
                {
                    description = string.Join("\n", new[] { description, "排序规则：" + column.Collation }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                }
                var primaryKey = StructureArchiveText.PrimaryKeyMarker(table, column);
                var foreignKey = StructureArchiveText.ForeignKeyMarker(archive.Project, table, column);
                var expandGeneration = generation.Length > 120 || generation.Count(c => c == '\n') > 3;
                var expandDescription = description.Length > 120 || description.Count(c => c == '\n') > 3;
                Row([column.Name, column.Label, StructureArchiveText.DataType(column), StructureArchiveText.Nullability(column),
                    expandGeneration ? "见下方生成说明" : generation, primaryKey, foreignKey,
                    expandDescription ? "见下方字段说明" : description], ColumnWidths, 8, repeat: Repeat,
                    padding: 2.5, centeredCells: CenteredKeyColumns);
                // 长表达式或备注占整行展示，避免单个窄单元格撑出大量几乎空白的页面。
                void Details(string label, string value)
                {
                    void ContinueDetails()
                    {
                        Repeat();
                        Paragraph($"字段 {column.Name} · {label}（续）", 8, true, background: Pale);
                    }
                    Paragraph($"字段 {column.Name} · {label}：{value}", 8, background: Pale, repeat: ContinueDetails);
                }
                if (expandGeneration)
                {
                    Details("生成说明", generation);
                }
                if (expandDescription)
                {
                    Details("字段说明", description);
                }
            }
            if (table.Columns.Count == 0)
            {
                Paragraph("此表尚无字段定义。", 8.2, repeat: Repeat);
            }
            var indexes = StructureArchiveText.IndexNotes(table).ToList();
            if (indexes.Count > 0)
            {
                Paragraph("索引", 8.2, true, background: Heading, repeat: Repeat, padding: 3);
                foreach (var index in indexes)
                {
                    Paragraph(index, 8, color: Muted, background: Pale, repeat: Repeat, padding: 2.5);
                }
            }
            var otherNotes = StructureArchiveText.OtherNotes(table).ToList();
            if (otherNotes.Count > 0)
            {
                foreach (var note in otherNotes)
                {
                    Paragraph(note, 8, color: Muted, background: Pale, repeat: Repeat, padding: 2.5);
                }
            }
            y += 12;
        }

        private void NewPage()
        {
            cancellationToken.ThrowIfCancellationRequested();
            graphics?.Dispose();
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            page.Orientation = PdfSharp.PageOrientation.Landscape;
            graphics = XGraphics.FromPdfPage(page);
            y = Top;
            graphics.DrawString("DB STUDIO  /  数据库结构归档", Font(8), new XSolidBrush(Muted), new XPoint(Left, 29));
        }

        private void Ensure(double height, Action? repeat = null)
        {
            if (y + height <= Bottom)
            {
                return;
            }
            NewPage();
            repeat?.Invoke();
        }

        private void Section(string title)
        {
            Ensure(85);
            y += 8;
            Paragraph(title, 11, true);
            y += 3;
        }

        private void CompactSection(string title)
        {
            Ensure(55);
            y += 4;
            Paragraph(title, 10.5, true, padding: 2.5);
            y += 1;
        }

        private void FormalTable(string[] headers, double[] widths, string[][] rows,
            double size = 9.3, double padding = 8, double bottomGap = 10)
        {
            void Repeat() => Row(headers, widths, size, true, Heading, padding: padding);
            Ensure(80);
            Repeat();
            var alternate = false;
            foreach (var row in rows)
            {
                Row(row, widths, size, background: alternate ? Pale : null, repeat: Repeat, padding: padding);
                alternate = !alternate;
            }
            y += bottomGap;
        }

        private void Paragraph(string value, double size, bool bold = false, XColor? color = null,
            XColor? background = null, Action? repeat = null, double padding = 4)
        {
            Row([value], [Width], size, bold, background, repeat, padding, color, rule: false);
        }

        // 普通行尽量整体移至下一页；高于整页的字段备注按行拆分，避免截断或无限换页。
        private void Row(string[] values, double[] widths, double size, bool bold = false, XColor? background = null,
            Action? repeat = null, double padding = 4, XColor? color = null, bool rule = true,
            IReadOnlySet<int>? centeredCells = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = values.Select((value, i) => Wrap(value, widths[i] - 12, size, bold)).ToArray();
            var count = lines.Max(list => list.Count);
            var leading = size * 1.48;
            var height = count * leading + padding * 2;
            if (height <= Bottom - Top - 110)
            {
                Ensure(height, repeat);
            }
            var offset = 0;
            while (offset < count)
            {
                Ensure(leading + padding * 2, repeat);
                var take = Math.Min(count - offset, (int)((Bottom - y - padding * 2) / leading));
                var segmentHeight = take * leading + padding * 2;
                if (background.HasValue)
                {
                    graphics.DrawRectangle(new XSolidBrush(background.Value), Left, y, Width, segmentHeight);
                }
                var x = Left;
                for (var cell = 0; cell < lines.Length; cell++)
                {
                    var centered = centeredCells?.Contains(cell) == true;
                    for (var line = 0; line < take && offset + line < lines[cell].Count; line++)
                    {
                        graphics.DrawString(lines[cell][offset + line], Font(size, bold), new XSolidBrush(color ?? (bold ? Navy : Ink)),
                            centered ? new XRect(x, y + padding + line * leading, widths[cell], leading)
                                : new XRect(x + 6, y + padding + line * leading, widths[cell] - 12, leading),
                            centered ? XStringFormats.TopCenter : XStringFormats.TopLeft);
                    }
                    x += widths[cell];
                }
                if (rule && widths.Length > 1)
                {
                    var dividerX = Left;
                    for (var cell = 0; cell < widths.Length - 1; cell++)
                    {
                        dividerX += widths[cell];
                        graphics.DrawLine(Border, dividerX, y, dividerX, y + segmentHeight);
                    }
                }
                y += segmentHeight;
                if (rule)
                {
                    graphics.DrawLine(Border, Left, y, Left + Width, y);
                }
                offset += take;
            }
        }

        private List<string> Wrap(string value, double width, double size, bool bold)
        {
            var result = new List<string>();
            foreach (var paragraph in value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n'))
            {
                var line = "";
                var used = 0d;
                var elements = StringInfo.GetTextElementEnumerator(paragraph);
                while (elements.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var element = elements.GetTextElement();
                    var key = (element, size, bold);
                    if (!measures.TryGetValue(key, out var measured))
                    {
                        measured = graphics.MeasureString(element, Font(size, bold)).Width;
                        measures[key] = measured;
                    }
                    if (used + measured > width && line.Length > 0)
                    {
                        var starts = StringInfo.ParseCombiningCharacters(line);
                        if ("，。；：！？、）】》”’".Contains(element, StringComparison.Ordinal) && starts.Length > 1)
                        {
                            var last = line[starts[^1]..];
                            result.Add(line[..starts[^1]]);
                            line = last + element;
                            used = graphics.MeasureString(line, Font(size, bold)).Width;
                            continue;
                        }
                        result.Add(line);
                        line = "";
                        used = 0;
                    }
                    line += element;
                    used += measured;
                }
                result.Add(line);
            }
            return result;
        }

        private XFont Font(double size, bool bold = false)
        {
            var key = (size, bold);
            if (!fonts.TryGetValue(key, out var font))
            {
                font = new XFont("DB Studio Sans", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                fonts[key] = font;
            }
            return font;
        }

        private static string Filled(string value) => string.IsNullOrWhiteSpace(value) ? "________________" : value.Trim();

        private static string Shorten(string value, int length)
        {
            var elements = StringInfo.ParseCombiningCharacters(value);
            return elements.Length <= length ? value : value[..elements[length]] + "…";
        }

        public void Dispose()
        {
            graphics?.Dispose();
            document.Dispose();
        }
    }
}
