# FluxLimiter UI Contract

Status: APPROVED. This file is the visual/interaction acceptance contract. Do not treat a successful build as visual approval.

## Visual reference
The approved reference screenshot is authoritative for layout language: dark navy canvas, slightly lighter cards, subtle borders, rounded surfaces, generous spacing, modern Windows typography. Do not ship a stock WinForms/DataGridView appearance.

## Typography and palette
Use Segoe UI Variable where available. Display/Bold for page headings and values, Semibold for section headings, Regular for body/metrics. No Consolas/monospace UI. Download is blue; upload is green. Avoid neon/magenta. Borders must be subtle.

## Sidebar
FluxLimiter brand at top. Dashboard, Applications, Rules, Settings navigation. Selected item is a rounded dark-blue surface with blue/cyan emphasis; unselected items are transparent and muted. Bottom shows Service Running and version.

## Dashboard header
Title: Dashboard. Subtitle: Live network usage and application activity. No clipping/overlap.

## Metric cards
Four cards: Download, Upload, Today’s Usage, Monthly Usage. Download/upload show live rate and lightweight sparkline. Today shows total plus download/upload breakdown. Monthly shows total plus breakdown when data is available.

## Seven-day chart
English weekday labels only (InvariantCulture). Show separate blue Download and green Upload bars/series with a legend and subtle horizontal guides.

## Active applications
Must not look like a stock DataGridView. Rows are spacious, borderless, with subtle separators/hover, real application icon/name, Status, live Download, live Upload, Total, Limit Download, Limit Upload, Rule, and an ellipsis action. Search/filter use dark rounded surfaces.

Per-app Download/Upload must update approximately once per second without rebuilding the full process list. Zero values are acceptable only when the measured process genuinely has no traffic; ETW attribution must be runtime validated.

Application action menu: Open file location; Open process folder; Copy process path; Open in Task Manager; Exclude from list. Do not add End Process.

## Top Applications
Show real highest-usage applications for today only after per-app measurement is validated. Never populate fake/demo traffic.

## Taskbar speed meter
Two lines only, Download and Upload. No FluxLimiter name. No visible rectangle/background. Text is monochrome: white on dark Windows taskbar, black on light. Use Segoe UI Variable Text Regular and keep it visually quiet.

Hover shows a custom FluxLimiter mini panel with Today’s Usage: download, upload, total. Do not use a legacy-looking tooltip in the final UI.

Right-click uses the same visual language as the app: Open FluxLimiter, limiter control when real enforcement exists, Settings, separator, Exit. Do not expose a fake Pause Limiter before enforcement exists.

The meter follows taskbar visibility/auto-hide and repositions on taskbar/resolution/DPI changes. Do not inject into Explorer.

## Branding
Official icon direction: concept #3 “Flow Limit”: download + upload + controlled flow motif, blue/cyan/green language. Produce a Windows multi-resolution icon (16,24,32,48,64,128,256) with small-size simplification. Apply to EXE, window, taskbar, Alt+Tab, tray, Start/Search, shortcuts, installer/uninstaller and notifications. Do not use SystemIcons.Shield.

## Performance
No heavy animation or glass/transparency in the dashboard. Double-buffer custom surfaces. Cache icons/paths. Update changed values in place. Reduce/stop refresh work while minimized. Keep taskbar meter lightweight.

## Acceptance
CI build/test/publish success is necessary but not visual acceptance. Final acceptance requires Windows runtime review at normal DPI and 125% DPI, including dashboard screenshot, per-app live traffic, taskbar light/dark behavior, hover, right-click menu, and taskbar auto-hide behavior.
