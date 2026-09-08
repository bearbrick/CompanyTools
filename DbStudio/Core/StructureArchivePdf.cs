using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace DbStudio.Core;

/// <summary>服务器独立生成可检索的中文 PDF；正式首尾页与紧凑正文共用分页、换行规则。</summary>
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
        private const double Width = 523;
        private const double Top = 49;
        private const double Bottom = 782;
        private static readonly XColor Ink = XColor.FromArgb(37, 56, 74);
        private static readonly XColor Navy = XColor.FromArgb(22, 50, 75);
        private static readonly XColor Muted = XColor.FromArgb(94, 111, 124);
        private static readonly XColor Pale = XColor.FromArgb(243, 247, 249);
        private static readonly XColor Heading = XColor.FromArgb(229, 239, 244);
        private static readonly XPen Border = new(XColor.FromArgb(217, 227, 233), 0.4);
        private static readonly double[] ColumnWidths = [108, 94, 60, 114, Width - 376];
        private readonly StructureArchive archive;
        private readonly CancellationToken cancellationToken;
        private readonly PdfDocument document = new();
        private readonly Dictionary<(double, bool), XFont> fonts = [];
        private readonly Dictionary<(string, double, bool), double> measures = [];
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
            Approval();
            graphics.Dispose();
            graphics = null!;
            for (var i = 0; i < document.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var footer = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append);
                footer.DrawLine(Border, Left, 801, Left + Width, 801);
                footer.DrawString($"DB Studio  |  {archive.Options.Version}  |  项目修订 r{archive.Project.Revision}", Font(8), new XSolidBrush(Muted), new XPoint(Left, 818));
                footer.DrawString($"{i + 1} / {document.PageCount}", Font(8), new XSolidBrush(Muted), new XRect(Left, 806, Width, 16), XStringFormats.CenterRight);
            }
            using var output = new MemoryStream();
            document.Save(output, false);
            return output.ToArray();
        }

        private void Cover()
        {
            NewPage();
            document.Outlines.Add("归档说明", page, true);
            y += 14;
            Paragraph("数据库结构归档说明书", 23, true);
            Paragraph(archive.Project.Name, 13, true);
            y += 15;
            FormalTable(["文档信息", "内容"], [132, Width - 132],
            [
                ["文档编号 / 版本", $"{Filled(archive.Options.DocumentNumber)} / {archive.Options.Version}"],
                ["文档状态 / 密级", $"待核验签署 / {Filled(archive.Options.Classification)}"],
                ["数据库 / 产品版本", $"{Filled(archive.Options.DatabaseName)} / SQL Server {archive.Options.DatabaseVersion}"],
                ["来源环境 / 设计基线", $"{Filled(archive.Options.Environment)} / 项目已保存设计 r{archive.Project.Revision}"],
                ["导出时间", archive.ExportedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)],
                ["编制部门 / 编制人", $"{Filled(archive.Options.Department)} / {Filled(archive.Options.PreparedBy)}"]
            ]);
            Section("一、归档范围与依据");
            FormalTable(["项目", "内容"], [132, Width - 132],
            [
                ["归档范围", $"{archive.Project.Tables.Count} 张表、{archive.Project.Tables.Sum(t => t.Columns.Count)} 个字段；包含主键、索引、外键、检查约束和设计说明。"],
                ["结构来源", "DB Studio 已保存的项目设计。本次未连接实际数据库；设计与实际环境的一致性留待核验。"],
                ["范围说明", "不含业务数据、账号和连接配置；视图、过程、函数等未纳入设计器的对象不在本文件归档范围。"]
            ]);
            Section("二、修订记录");
            FormalTable(["文档版本", "项目修订", "修订说明 / 变更单"], [92, 85, Width - 177],
            [
                [archive.Options.Version, $"r{archive.Project.Revision}", "按当前已保存设计生成；变更单：________________"]
            ]);
            Section("三、阅读说明");
            Paragraph("正文按模块连续排列，多张小表共用一页，长表续页重复表名和表头；可通过 PDF 书签定位数据表。末页保留核验、附件登记与审批签署。", 9.3);
        }

        private void Approval()
        {
            NewPage();
            document.Outlines.Add("核验与归档确认", page, true);
            y += 14;
            Paragraph("核验与归档确认", 23, true);
            Paragraph($"文档编号：{Filled(archive.Options.DocumentNumber)}    版本：{archive.Options.Version}", 9.3);
            y += 14;
            Section("一、核验记录");
            FormalTable(["核验项目", "结果", "核验人 / 日期"], [280, 68, Width - 348],
            [
                ["对象清单、字段定义与归档范围一致", "待核验", "________________"],
                ["主键、索引、外键及检查约束完整", "待核验", "________________"],
                ["字段含义、取值规则已由业务确认", "待核验", "________________"],
                ["实际数据库与设计基线的一致性已核对", "待核验", "________________"]
            ]);
            Section("二、配套附件登记");
            FormalTable(["附件", "文件名 / 存放位置"], [132, Width - 132],
            [
                ["结构 SQL（如另行归档）", "____________________________________________"],
                ["项目 JSON / 可编辑源稿", "____________________________________________"],
                ["校验值清单 / 其他对象", "____________________________________________"]
            ]);
            Section("三、审批与归档");
            FormalTable(["角色", "签名 / 意见", "日期"], [88, 317, Width - 405],
            [
                ["编制", "________________ / ________________", "____________"],
                ["审核", "________________ / ________________", "____________"],
                ["批准", "________________ / ________________", "____________"],
                ["归档责任人", "________________ / ________________", "____________"]
            ]);
            Paragraph("档案编号 / 保存期限：________________________________________________", 9.3);
            Paragraph("本文件生成时尚未完成审批。完成核验及签署后归档；后续结构变更应形成新版本，保留历史文件及关联变更记录。", 9.3);
        }

        private void DrawTable(TableDesign table, int number)
        {
            cancellationToken.ThrowIfCancellationRequested();
            void Header(bool continued)
            {
                Paragraph($"{number:00}  {table.Schema}.{table.Name}{(continued ? "（续）" : "")}", 10, true, background: Heading);
                Row(["字段名", "类型", "空值限制", "默认 / 生成", "说明"], ColumnWidths, 8.2, true, Pale);
            }
            void Repeat() => Header(true);
            Ensure(105);
            document.Outlines.Add($"{number:00} {table.Schema}.{table.Name}", page, false);
            Paragraph($"{number:00}  {table.Schema}.{table.Name}", 10, true, background: Heading);
            Paragraph($"模块：{table.Module}" + (table.Label == "" ? "" : $"  |  {table.Label}"), 8, color: Muted, repeat: Repeat);
            if (!string.IsNullOrWhiteSpace(table.Comment))
            {
                Paragraph("备注：" + table.Comment, 8, color: Muted, repeat: Repeat);
            }
            if (y + 40 > Bottom)
            {
                NewPage();
                Paragraph($"{number:00}  {table.Schema}.{table.Name}（续）", 10, true, background: Heading);
            }
            Row(["字段名", "类型", "空值限制", "默认 / 生成", "说明"], ColumnWidths, 8.2, true, Pale);
            foreach (var column in table.Columns)
            {
                var generation = StructureArchiveText.Generation(column);
                var description = StructureArchiveText.Description(column);
                var expandGeneration = generation.Length > 120 || generation.Count(c => c == '\n') > 3;
                var expandDescription = description.Length > 120 || description.Count(c => c == '\n') > 3;
                Row([column.Name, StructureArchiveText.DataType(column), StructureArchiveText.Nullability(column), expandGeneration ? "见下方生成说明" : generation, expandDescription ? "见下方字段说明" : description], ColumnWidths, 8.2, repeat: Repeat);
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
            foreach (var note in StructureArchiveText.Notes(archive.Project, table))
            {
                Paragraph(note, 8, color: Muted, background: Pale, repeat: Repeat);
            }
            y += 12;
        }

        private void NewPage()
        {
            cancellationToken.ThrowIfCancellationRequested();
            graphics?.Dispose();
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
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

        private void FormalTable(string[] headers, double[] widths, string[][] rows)
        {
            void Repeat() => Row(headers, widths, 9.3, true, Heading, padding: 8);
            Ensure(80);
            Repeat();
            var alternate = false;
            foreach (var row in rows)
            {
                Row(row, widths, 9.3, background: alternate ? Pale : null, repeat: Repeat, padding: 8);
                alternate = !alternate;
            }
            y += 10;
        }

        private void Paragraph(string value, double size, bool bold = false, XColor? color = null, XColor? background = null, Action? repeat = null)
        {
            Row([value], [Width], size, bold, background, repeat, color: color, rule: false);
        }

        // 普通行尽量整体移至下一页；高于整页的字段备注按行拆分，避免截断或无限换页。
        private void Row(string[] values, double[] widths, double size, bool bold = false, XColor? background = null, Action? repeat = null, double padding = 4, XColor? color = null, bool rule = true)
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
                    for (var line = 0; line < take && offset + line < lines[cell].Count; line++)
                    {
                        graphics.DrawString(lines[cell][offset + line], Font(size, bold), new XSolidBrush(color ?? (bold ? Navy : Ink)), new XRect(x + 6, y + padding + line * leading, widths[cell] - 12, leading), XStringFormats.TopLeft);
                    }
                    x += widths[cell];
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

        public void Dispose()
        {
            graphics?.Dispose();
            document.Dispose();
        }
    }
}
