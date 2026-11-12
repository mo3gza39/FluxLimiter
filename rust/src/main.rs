#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
//! FluxLimiter - entirely-Rust edition.
//!
//! Layout follows the approved dashboard design: dark sidebar, four
//! stat cards, Top Applications, 7-day stacked chart, Active
//! Applications table with per-app limit dropdowns. Plus the
//! TrafficMonitor-style taskbar speed strip (plain text, no themes).
//! Speeds and limits are KB/s by the user's decision (settings can
//! switch the speed display to bits).

use eframe::egui;
use egui::{Color32, RichText, Stroke, Vec2};
use fluxlimiter::format::{format_bytes, format_speed, limit_label, parse_limit_kbps, LIMIT_PRESETS_KBPS};
use fluxlimiter::monitor::{NetworkMonitor, Snapshot};
use fluxlimiter::rules::{Rule, RulesStore};
use fluxlimiter::stats::StatsStore;
use std::time::{Duration, Instant};

const BG: Color32 = Color32::from_rgb(10, 22, 34);
const CARD: Color32 = Color32::from_rgb(16, 32, 47);
const BORDER: Color32 = Color32::from_rgb(27, 52, 72);
const MUTED: Color32 = Color32::from_rgb(138, 160, 179);
const GREEN: Color32 = Color32::from_rgb(34, 197, 94);
const BLUE: Color32 = Color32::from_rgb(59, 130, 246);
const MONTHLY_QUOTA_GB: u64 = 200;

fn main() -> eframe::Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1240.0, 800.0])
            .with_title("FluxLimiter"),
        ..Default::default()
    };
    eframe::run_native(
        "FluxLimiter",
        options,
        Box::new(|cc| {
            let mut visuals = egui::Visuals::dark();
            visuals.panel_fill = BG;
            visuals.window_fill = BG;
            cc.egui_ctx.set_visuals(visuals);
            Ok(Box::new(FluxApp::default()))
        }),
    )
}

struct FluxApp {
    monitor: NetworkMonitor,
    stats: StatsStore,
    rules: RulesStore,
    snap: Snapshot,
    last_poll: Instant,
    use_bits: bool,
    show_strip: bool,
    search: String,
}

impl Default for FluxApp {
    fn default() -> Self {
        let mut monitor = NetworkMonitor::new();
        let snap = monitor.poll();
        Self {
            monitor,
            stats: StatsStore::load_default(),
            rules: RulesStore::load_default(),
            snap,
            last_poll: Instant::now(),
            use_bits: false,
            show_strip: true,
            search: String::new(),
        }
    }
}

impl FluxApp {
    fn poll_if_due(&mut self) {
        if self.last_poll.elapsed() >= Duration::from_secs(1) {
            self.snap = self.monitor.poll();
            self.stats.add(self.snap.down_delta, self.snap.up_delta);
            self.stats.flush();
            self.last_poll = Instant::now();
        }
    }

    fn card<R>(ui: &mut egui::Ui, add: impl FnOnce(&mut egui::Ui) -> R) {
        egui::Frame::none()
            .fill(CARD)
            .stroke(Stroke::new(1.0_f32, BORDER))
            .rounding(12.0)
            .inner_margin(14.0)
            .show(ui, add);
    }

    fn taskbar_strip(&self, ctx: &egui::Context) {
        if !self.show_strip {
            return;
        }
        let down = format_speed(self.snap.down_bps, self.use_bits);
        let up = format_speed(self.snap.up_bps, self.use_bits);
        ctx.show_viewport_immediate(
            egui::ViewportId::from_hash_of("taskbar_strip"),
            egui::ViewportBuilder::default()
                .with_title("FluxLimiter speed")
                .with_inner_size([150.0, 46.0])
                .with_decorations(false)
                .with_always_on_top()
                .with_taskbar(false),
            move |vctx, _| {
                egui::CentralPanel::default()
                    .frame(egui::Frame::none().fill(Color32::from_rgb(11, 21, 32)))
                    .show(vctx, |ui| {
                        ui.label(RichText::new(format!("↓ {down}")).color(GREEN).size(13.0));
                        ui.label(RichText::new(format!("↑ {up}")).color(BLUE).size(13.0));
                    });
                if vctx.input(|i| i.viewport().close_requested()) {
                    // strip is controlled from the main window checkbox
                }
            },
        );
    }
}

impl eframe::App for FluxApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll_if_due();
        ctx.request_repaint_after(Duration::from_millis(250));
        self.taskbar_strip(ctx);

        egui::SidePanel::left("sidebar")
            .exact_width(200.0)
            .frame(egui::Frame::none().fill(Color32::from_rgb(12, 26, 41)))
            .show(ctx, |ui| {
                ui.add_space(14.0);
                ui.label(RichText::new("▮▮ FluxLimiter").size(17.0).strong());
                ui.add_space(16.0);
                for (i, name) in ["Dashboard", "Applications", "Rules", "Monthly Usage", "Statistics"]
                    .iter()
                    .enumerate()
                {
                    let selected = i == 0;
                    let btn = egui::Button::new(RichText::new(*name).size(13.0))
                        .fill(if selected { Color32::from_rgb(11, 94, 215) } else { Color32::TRANSPARENT })
                        .rounding(9.0)
                        .min_size(Vec2::new(168.0, 38.0));
                    ui.add(btn);
                    ui.add_space(4.0);
                }
                ui.add_space(24.0);
                ui.checkbox(&mut self.show_strip, RichText::new("Taskbar speed strip").color(MUTED));
                ui.checkbox(&mut self.use_bits, RichText::new("Show speeds in bits").color(MUTED));
                ui.add_space(8.0);
                ui.label(
                    RichText::new("Limits are saved, not enforced yet\n(driver engine is the next milestone).")
                        .color(MUTED)
                        .size(11.0),
                );
            });

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.label(RichText::new("Dashboard").size(26.0).strong());
            ui.label(RichText::new("Overview of your network usage and active applications").color(MUTED));
            ui.add_space(12.0);

            // ---- stat cards ----
            let (d_down, d_up) = self.stats.today();
            let (m_down, m_up) = self.stats.current_month();
            let month_total = m_down + m_up;
            let quota = MONTHLY_QUOTA_GB * 1024 * 1024 * 1024;
            ui.columns(4, |cols| {
                Self::card(&mut cols[0], |ui| {
                    ui.label(RichText::new("↓ Download").color(MUTED));
                    ui.label(RichText::new(format_speed(self.snap.down_bps, self.use_bits)).size(19.0).strong());
                });
                Self::card(&mut cols[1], |ui| {
                    ui.label(RichText::new("↑ Upload").color(MUTED));
                    ui.label(RichText::new(format_speed(self.snap.up_bps, self.use_bits)).size(19.0).strong());
                });
                Self::card(&mut cols[2], |ui| {
                    ui.label(RichText::new("Daily Usage").color(MUTED));
                    ui.label(RichText::new(format_bytes(d_down + d_up)).size(19.0).strong());
                    ui.label(RichText::new(format!("↓ {}   ↑ {}", format_bytes(d_down), format_bytes(d_up))).color(MUTED).size(11.0));
                });
                Self::card(&mut cols[3], |ui| {
                    ui.label(RichText::new("Monthly Usage").color(MUTED));
                    ui.label(RichText::new(format!("{} / {} GB", format_bytes(month_total), MONTHLY_QUOTA_GB)).size(19.0).strong());
                    ui.label(RichText::new(format!("{:.0}% of monthly quota", month_total as f64 / quota as f64 * 100.0)).color(MUTED).size(11.0));
                });
            });
            ui.add_space(12.0);

            // ---- middle row ----
            ui.columns(2, |cols| {
                Self::card(&mut cols[0], |ui| {
                    ui.label(RichText::new("Top Applications (active connections)").strong());
                    ui.add_space(6.0);
                    let top: Vec<_> = self.snap.apps.iter().take(5).collect();
                    if top.is_empty() {
                        ui.label(RichText::new("No active connections right now.").color(MUTED));
                    }
                    let peak = top.iter().map(|a| a.connections).max().unwrap_or(1).max(1);
                    for app in top {
                        ui.horizontal(|ui| {
                            ui.label(&app.name);
                            let w = 160.0 * app.connections as f32 / peak as f32;
                            let (rect, _) = ui.allocate_exact_size(Vec2::new(170.0, 9.0), egui::Sense::hover());
                            ui.painter().rect_filled(rect, 4.0, BORDER);
                            let mut fill = rect;
                            fill.max.x = rect.min.x + w.max(6.0);
                            ui.painter().rect_filled(fill, 4.0, GREEN);
                            ui.label(RichText::new(format!("{} conn", app.connections)).color(MUTED));
                        });
                    }
                });
                Self::card(&mut cols[1], |ui| {
                    ui.label(RichText::new("Daily Usage (Last 7 Days)   ■ Download  ■ Upload").strong());
                    ui.add_space(6.0);
                    let days = self.stats.last_days(7);
                    let peak = days.iter().map(|(_, r, s)| r + s).max().unwrap_or(1).max(1) as f32;
                    let (rect, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 150.0), egui::Sense::hover());
                    let slot = rect.width() / days.len() as f32;
                    for (i, (date, r, s)) in days.iter().enumerate() {
                        let total_h = (*r + *s) as f32 / peak * 118.0;
                        let down_h = *r as f32 / peak * 118.0;
                        let cx = rect.min.x + slot * i as f32 + slot / 2.0;
                        let bw = (slot * 0.5).min(42.0);
                        let base = rect.max.y - 20.0;
                        ui.painter().rect_filled(
                            egui::Rect::from_min_max(
                                egui::pos2(cx - bw / 2.0, base - down_h),
                                egui::pos2(cx + bw / 2.0, base),
                            ),
                            3.0, GREEN);
                        ui.painter().rect_filled(
                            egui::Rect::from_min_max(
                                egui::pos2(cx - bw / 2.0, base - total_h),
                                egui::pos2(cx + bw / 2.0, base - down_h),
                            ),
                            3.0, BLUE);
                        ui.painter().text(
                            egui::pos2(cx, base + 4.0),
                            egui::Align2::CENTER_TOP,
                            &date[5..],
                            egui::FontId::proportional(10.0),
                            MUTED);
                    }
                });
            });
            ui.add_space(12.0);

            // ---- active applications table ----
            Self::card(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(RichText::new("Active Applications").size(15.0).strong());
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.add(egui::TextEdit::singleline(&mut self.search).hint_text("Search applications…").desired_width(220.0));
                    });
                });
                ui.add_space(6.0);
                egui::ScrollArea::vertical().show(ui, |ui| {
                    let query = self.search.to_lowercase();
                    let apps: Vec<_> = self.snap.apps.iter()
                        .filter(|a| query.is_empty() || a.name.to_lowercase().contains(&query))
                        .cloned()
                        .collect();
                    if apps.is_empty() {
                        ui.label(RichText::new("No applications with active connections.").color(MUTED));
                    }
                    for app in apps {
                        let mut rule: Rule = self.rules.get_or_default(&app.exe_path, &app.name);
                        let mut changed = false;
                        ui.horizontal(|ui| {
                            ui.add_sized([240.0, 20.0], egui::Label::new(&app.name));
                            ui.add_sized([90.0, 20.0], egui::Label::new(RichText::new("● Active").color(GREEN)));
                            ui.add_sized([90.0, 20.0], egui::Label::new(format!("{} conn", app.connections)));
                            // limit dropdowns (KB/s presets)
                            for is_down in [true, false] {
                                let current = if is_down { rule.limit_down_kbps } else { rule.limit_up_kbps };
                                let mut label = limit_label(current);
                                egui::ComboBox::from_id_salt(format!("lim-{}-{is_down}", app.exe_path))
                                    .selected_text(label.clone())
                                    .width(105.0)
                                    .show_ui(ui, |ui| {
                                        for preset in LIMIT_PRESETS_KBPS {
                                            if ui.selectable_value(&mut label, preset.to_string(), preset).changed() {
                                                let v = parse_limit_kbps(&label);
                                                if is_down { rule.limit_down_kbps = v; } else { rule.limit_up_kbps = v; }
                                                changed = true;
                                            }
                                        }
                                    });
                            }
                            ui.label(if rule.blocked {
                                RichText::new("Block").color(Color32::from_rgb(248, 113, 113))
                            } else if rule.has_limit() {
                                RichText::new("Limit")
                            } else {
                                RichText::new("—").color(MUTED)
                            });
                            if ui.small_button(if rule.blocked { "Unblock" } else { "Block" }).clicked() {
                                rule.blocked = !rule.blocked;
                                changed = true;
                            }
                        });
                        if changed {
                            self.rules.set(rule);
                        }
                    }
                });
            });
        });
    }
}
