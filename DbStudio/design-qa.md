# DB Studio V1 design QA

final result: passed

## Evidence

- Source visual truth: `docs/screenshots/reference.png` (user-selected ImageGen concept).
- Implementation: `docs/screenshots/workspace-desktop.png`.
- Comparison viewport: 1586 × 992 CSS pixels. Source and screenshot both 1586 × 992 pixels, effective density 1. No resizing in the comparison artifacts.
- State: administrator, 盘起工业 ERP, 客户 / ERP_Sales_Customer, field definitions, Name row selected, database-tools menu open. The source shows an unsaved focused cell; final screenshot shows saved data with focus moved to the menu. This state difference is intentional and does not imply an editing failure.
- Full comparison: `docs/screenshots/comparison-final.png`, source left, implementation right.
- Focused grid comparison: `docs/screenshots/comparison-grid.png`, source top, implementation bottom. Used to inspect row rhythm, input readability, rules, checkbox alignment and type labels at full density.
- Additional screenshots: `column-settings.png`, `rbac.png`, `workspace-compact.png` in the same directory.
- Browser: Codex in-app browser. Compact viewport tested at 900 × 800; viewport override reset after verification.

## Findings and resolution history

1. [P2, resolved] Initial implementation used 12px grid labels and subdued text; the reference gave the grid more readable typography. Set editable Chinese cells and table headers to 14px, identifiers to 13px monospace, and sidebar/tab labels to 14px. Verified in the focused final comparison.
2. [P2, resolved] Initial Add Row control was below the full field list and could disappear below the viewport. Split the grid scroll container from the fixed add-row footer. The final screenshot shows Add Row at the bottom while the remaining fields scroll internally.
3. [P2, resolved] Initial wide column allocation pushed remarks and advanced settings out of the reference viewport. Rebalanced columns, retained a readable 1180px minimum grid, and kept horizontal scrolling inside the grid for narrower windows. All columns fit the reference viewport; the save controls and sidebar remain visible at 900px.

Before-fix evidence is `comparison-before.png`; post-fix evidence is `comparison-final.png` and `comparison-grid.png`. No outstanding P0/P1/P2 visual findings remain for the desktop scope.

## Fidelity surfaces

- Typography: Microsoft YaHei / Segoe UI for Chinese UI, Consolas for identifiers. Headings retain the source hierarchy. Grid labels are readable at the target viewport; long remarks use input truncation and full-value title/editing.
- Spacing/layout: navy 64px header, pale left tree, white main workspace, compact actions above the grid, fine row/column dividers. Actual module/table names come from the workbook, so sidebar density differs from the illustrative source list.
- Colors/tokens: restrained navy/blue/white palette, pale blue selected row, blue checkbox/focus/primary save, understated metadata. Future tools are disabled and therefore lighter than the mock.
- Assets: real MIT Bootstrap Icons used consistently as local SVG assets; no remote runtime font or image dependency. No illustrative raster assets are needed for this editor.
- Copy/content: all 99 imported tables and 1,778 fields retained. DDL-specific tabs and RBAC added as requested after the visual selection. Future database operations explicitly marked disabled. No false claim of an active database connection.

## Interaction verification

- Cookie administrator login; authenticated page loads real project data.
- Edit customer display label, save, read stored SQLite data and reload; original label restored after testing.
- Ctrl+S produces a confirmed saved revision.
- Add field, then cancel/discard using the app confirmation dialog; restored to 30 source fields.
- Paste two TSV rows, observe 32 fields, discard and return to 30 fields.
- Define a CurrencyId → ERP_Base_Currency.Id foreign key and generate the corresponding ALTER TABLE statement; no validation errors. This test relationship was not persisted into the user's imported design.
- Advanced-column drawer opens with identity, composite-key order, computed expression, persistence, default and collation controls.
- RBAC user list and permission matrix render; protected administrator role is disabled for editing. Service tests cover permission mutations and enforcement.
- SQL generation and errors, role management view, menu states, project/table navigation, internal grid scrolling and modal focus observed.
- Browser console reviewed. Logs during deliberate development-server restarts contain expected connection-negotiation failures; final reload reconnected successfully at 2026-09-07 09:26:16 UTC, with no subsequent warning/error entries observed during final interactions.

## Technical verification and limits

- Solution build: 0 warnings, 0 errors.
- 54 isolated C# checks passed, including all 99 imported scripts parsed by Microsoft ScriptDom, persistence, concurrency, authentication, RBAC revocation and DDL relationship checks.
- 9 HTTP checks passed: unauthenticated API rejection, cookie login, authenticated project read, CSRF rejection, invalid structure rejection, and static resource delivery.
- No live SQL Server connection or script execution was tested; these functions are outside V1 and clearly disabled. Script parsing is not database execution validation.

## Follow-up polish

- [P3] User-configurable column widths and visibility, sticky identifier columns, and project-scoped membership would be useful additions after first user feedback.
- Phone workflows are not a primary V1 target; this QA covers desktop and a narrower desktop window, not a complete mobile accessibility audit.

## Implementation checklist

- [x] Resolve selected mock and use real workbook data.
- [x] Fix above-the-fold readability and add-row access.
- [x] Verify key browser interactions and server-side enforcement.
- [x] Compare final source/implementation and record evidence.
- [x] Restore source data after temporary UI tests and reset viewport override.

## 2026-09-07 · 全站选择器与后台排版更新

- 统一使用 StudioSelect；字段类型提供用途与分类，引用表同时检索逻辑名、物理名和模块，角色显示权限摘要。
- 实测键盘方向键打开、搜索过滤、Enter 提交、Escape 取消、Tab 移动到下一字段、点击外部关闭。
- 实测成员弹窗内 Escape 只关闭选择器；鼠标选中角色只更新表单，未保存成员或权限。
- 实测 `Currency` 匹配 2 / 99 张引用表；无结果显示明确空态；复合字段 `CurrencyId, CountryId` 可输入并回填。
- 900×700 浮层边界：left 578、right 888、top 47、bottom 457，完全位于视口内；1440×900 桌面布局已截图。完成后恢复浏览器默认尺寸。
- 54 项隔离服务回归通过；额外 9 项 HTTP 检查通过（登录、匿名拦截、防伪、结构错误、静态资源）。构建零警告、零错误，dotnet format 校验通过。
- 所有测试设计草稿均撤销，客户表仍为 r6、30 字段、0 外键。原 Visual Studio 调试进程保留；新版独立运行于 5188。

实际截图：`docs/screenshots/dropdown-types.png`。

## 紧凑顶部、整表复制与设计预检查

- 1440×900 同尺寸实测：顶部信息区 204.59 → 113.69px；页签 48 → 40px；字段表格起始位置 385.59 → 270.69px，释放约 115px。
- 900×700 检查：页面宽度仍为 900px，保存按钮位于 x774–876，顶部无页面横向溢出。
- 正常客户表预检查通过；临时将 Name 长度改为 0 后，清单准确显示字段名称和长度规则。
- 未保存编辑复制前会确认，取消保留原草稿；确认后生成客户副本、默认表名 ERP_Sales_Customer_copy，并保留 30 个字段。
- 新表草稿在表信息页明确标识，尚未持久化时不展示删除已保存设计的操作。
- 62 项回归通过：新增副本 ID 隔离、属性保留、自引用重定向、源数据不变、重名处理、标识符长度、预检查和入站外键覆盖。构建零警告、零错误。
- 浏览器验证草稿已撤销，源客户表保持 r6、30 字段、0 外键；测试未向实际项目保存副本。

## 项目 JSON 备份恢复验证

- 82 项检查通过，新增预览无写入、恢复为新项目、ID 隔离、外键/自引用重映射、重复导入、无效内容拦截、权限边界及失败原子性测试。
- 5189 独立临时数据库进行浏览器验证：选择 JSON 文件 → 自动预览 2 张表/5 个字段/1 个外键 → 设置名称 → 创建项目。成功切换至 r1 新项目，Product 外键仍指向 Category，删除规则 SET NULL，CHECK 保留。
- 空对象 JSON 在界面提示缺少 Name、Dialect、Tables，提交按钮禁用。
- 预览通过后自动折叠 JSON 原文，保留表清单和项目名；编辑原文使旧预览失效。
- 原 5188 数据未导入测试项目；临时测试库与正式预览目录隔离。
- 最终构建实际项目预览：99 张表、1778 字段全部检查通过，未提交到正式数据。编辑 JSON 后旧预览立即清空，提交按钮禁用。
- 最终 UI 截图：`docs/screenshots/project-import.png`。预览结束后关闭弹窗、恢复浏览器默认尺寸，正式项目仍保持原样。
