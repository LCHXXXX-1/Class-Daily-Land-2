// 已删除：课表编辑器不再是设置中心的页内浮层（TimetableDialog）。
//
// 现在它是设置中心左侧导航里的一级页「课表编辑」——
// 见 Views\Pages\TimetableEditorPage.xaml(.cs)：
//   · 7 天全列（模型本来就存 7 天，旧浮层只放 5 天）；
//   · 编辑的是本地副本，点「保存」才写回 schedule.json（旧浮层是失焦即存，改一半也会落盘）；
//   · 导入/导出仍走 ScheduleManager.ExportJson / ImportJson，JSON 结构未变。
//
// 本文件只剩这段说明，可以直接删掉。
