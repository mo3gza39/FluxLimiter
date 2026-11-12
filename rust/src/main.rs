#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
//! FluxLimiter - entirely-Rust edition.
//!
//! Layout follows the approved dashboard design: dark sidebar, four
//! stat cards, Top Applications, 7-day stacked chart, Active
//! Applications table with per-app limit dropdowns. Plus the
//! TrafficMonitor-style taskbar speed strip (plain text, no themes).
//! Speeds and limits are KB/s by the user's decision (settings can
//! switch the speed display to bits).
//!
//! Design language: one card style (fill + 1px border + 12px
//! rounding), small muted labels over large values, real sparklines
//! from the measured 1-second samples, an honest amber banner while
//! limits are stored-but-not-enforced, and ASCII-only text (egui's
//! default font has no arrows/emoji glyphs).

use eframe::egui;
use egui::{Color32, RichText, Stroke, Vec2};
use fluxlimiter::format::{format_bytes, format_speed, limit_label, parse_limit_kbps, LIMIT_PRESETS_KBPS};
use fluxlimiter::monitor::{AppTraffic, NetworkMonitor, Snapshot};
use fluxlimiter::rules::{Rule, RulesStore};
use fluxlimiter::stats::StatsStore;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

/// Decode base85 text (the project's sync channel carries text
/// only, so binary assets travel as base85).
fn b85_decode(text: &str) -> Vec<u8> {
    // Python base64.b85 alphabet (index = digit value).
    const ALPHABET: &[u8] = b"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~";
    let mut rev = [u8::MAX; 256];
    for (i, &c) in ALPHABET.iter().enumerate() {
        rev[c as usize] = i as u8;
    }
    let digits: Vec<u8> = text
        .bytes()
        .filter_map(|b| match rev[b as usize] {
            u8::MAX => None,
            v => Some(v),
        })
        .collect();
    let mut out = Vec::with_capacity(digits.len() * 4 / 5 + 4);
    for chunk in digits.chunks(5) {
        let mut acc: u64 = 0;
        for &d in chunk {
            acc = acc * 85 + d as u64;
        }
        let nbytes = if chunk.len() == 5 {
            4
        } else {
            for _ in chunk.len()..5 {
                acc = acc * 85 + 84;
            }
            chunk.len() - 1
        };
        let be = (acc as u32).to_be_bytes();
        out.extend_from_slice(&be[..nbytes]);
    }
    out
}

/// The application icon: the blue bars mark from the project's
/// reference design, stored as 64x64 RGBA in base85.
fn app_icon() -> egui::IconData {
    let rgba = b85_decode(include_str!("../assets/icon_rgba.b85"));
    if rgba.len() == 64 * 64 * 4 {
        egui::IconData { rgba, width: 64, height: 64 }
    } else {
        egui::IconData::default()
    }
}

/// Actions the taskbar strip's context menu can request; the main
/// window drains this once per frame.
#[derive(Clone, Copy, PartialEq)]
enum StripAction {
    FocusMain,
    ToggleBits,
    HideStrip,
    Exit,
}

const BG: Color32 = Color32::from_rgb(10, 22, 34);
const SIDEBAR_BG: Color32 = Color32::from_rgb(12, 26, 41);
const CARD: Color32 = Color32::from_rgb(16, 32, 47);
const CARD_INNER: Color32 = Color32::from_rgb(21, 40, 58);
const BORDER: Color32 = Color32::from_rgb(27, 52, 72);
const ROW_LINE: Color32 = Color32::from_rgb(23, 44, 62);
const MUTED: Color32 = Color32::from_rgb(138, 160, 179);
const TEXT: Color32 = Color32::from_rgb(226, 238, 246);
const GREEN: Color32 = Color32::from_rgb(34, 197, 94);
const BLUE: Color32 = Color32::from_rgb(59, 130, 246);
const AMBER: Color32 = Color32::from_rgb(245, 178, 66);
const AMBER_BG: Color32 = Color32::from_rgb(46, 36, 16);
const RED: Color32 = Color32::from_rgb(248, 113, 113);
const MONTHLY_QUOTA_GB: u64 = 200;
const HISTORY_LEN: usize = 40;

fn main() -> eframe::Result<()> {
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1240.0, 800.0])
            .with_min_inner_size([980.0, 640.0])
            .with_icon(app_icon())
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
    /// Rolling per-second (down, up) samples for the card sparklines.
    history: Vec<(f32, f32)>,
    /// Pending request from the taskbar strip's context menu.
    strip_action: Arc<Mutex<Option<StripAction>>>,
    /// Whether the strip has been given its initial position
    /// (bottom-right, next to the tray) yet.
    strip_placed: bool,
    /// The real app icon as a GPU texture for the sidebar logo,
    /// loaded once from the same RGBA bytes as the window icon.
    logo: Option<egui::TextureHandle>,
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
            history: Vec::new(),
            strip_action: Arc::new(Mutex::new(None)),
            strip_placed: false,
            logo: None,
        }
    }
}

impl FluxApp {
    fn poll_if_due(&mut self) {
        if self.last_poll.elapsed() >= Duration::from_secs(1) {
            self.snap = self.monitor.poll();
            self.stats.add(self.snap.down_delta, self.snap.up_delta);
            self.stats.flush();
            self.history.push((self.snap.down_bps as f32, self.snap.up_bps as f32));
            if self.history.len() > HISTORY_LEN {
                self.history.remove(0);
            }
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

    /// Small muted card label, the design's section voice.
    fn card_label(ui: &mut egui::Ui, text: &str, color: Color32) {
        ui.label(RichText::new(text.to_uppercase()).color(color).size(11.0).strong());
    }

    /// Thin rounded progress bar in the card style.
    fn progress_bar(ui: &mut egui::Ui, frac: f32, color: Color32) {
        let w = ui.available_width();
        let (rect, _) = ui.allocate_exact_size(Vec2::new(w, 7.0), egui::Sense::hover());
        ui.painter().rect_filled(rect, 3.5, CARD_INNER);
        if frac > 0.0 {
            let mut fill = rect;
            fill.max.x = rect.min.x + w * frac.clamp(0.0, 1.0);
            ui.painter().rect_filled(fill, 3.5, color);
        }
    }

    /// Sparkline of one history component (0 = down, 1 = up).
    fn sparkline(ui: &mut egui::Ui, history: &[(f32, f32)], idx: usize, color: Color32) {
        let w = ui.available_width();
        let (rect, _) = ui.allocate_exact_size(Vec2::new(w, 30.0), egui::Sense::hover());
        ui.painter().rect_filled(rect, 6.0, CARD_INNER);
        if history.len() < 2 {
            return;
        }
        let peak = history
            .iter()
            .map(|s| if idx == 0 { s.0 } else { s.1 })
            .fold(1.0_f32, f32::max);
        let step = rect.width() / (HISTORY_LEN - 1) as f32;
        let start_x = rect.min.x;
        let points: Vec<egui::Pos2> = history
            .iter()
            .enumerate()
            .map(|(i, s)| {
                let v = if idx == 0 { s.0 } else { s.1 };
                let y = rect.max.y - 3.0 - (v / peak) * (rect.height() - 6.0);
                egui::pos2(start_x + step * i as f32, y)
            })
            .collect();
        ui.painter().add(egui::Shape::line(points, Stroke::new(1.5_f32, color)));
    }

    fn taskbar_strip(&mut self, ctx: &egui::Context) {
        if !self.show_strip {
            return;
        }
        let down = format_speed(self.snap.down_bps, self.use_bits);
        let up = format_speed(self.snap.up_bps, self.use_bits);
        let (t_down, t_up) = self.stats.today();
        let (m_down, m_up) = self.stats.current_month();
        let hover_text = format!(
            "Today: Down {} / Up {}\nThis month: {}",
            format_bytes(t_down),
            format_bytes(t_up),
            format_bytes(m_down + m_up)
        );
        let use_bits = self.use_bits;
        let action_slot = self.strip_action.clone();
        let mut builder = egui::ViewportBuilder::default()
            .with_title("FluxLimiter speed")
            .with_inner_size([200.0, 46.0])
            .with_decorations(false)
            .with_always_on_top()
            .with_taskbar(false);
        if !self.strip_placed {
            // First run only: start next to the tray (bottom-right
            // of the monitor, above the taskbar) instead of the
            // OS default spot. After that the user can drag it
            // wherever they like and it stays there.
            if let Some(monitor) = ctx.input(|i| i.viewport().monitor_size) {
                builder = builder.with_position([
                    (monitor.x - 216.0).max(0.0),
                    (monitor.y - 100.0).max(0.0),
                ]);
            }
            self.strip_placed = true;
        }
        ctx.show_viewport_immediate(
            egui::ViewportId::from_hash_of("taskbar_strip"),
            builder,
            move |vctx, _| {
                egui::CentralPanel::default()
                    .frame(egui::Frame::none().fill(Color32::from_rgb(11, 21, 32)))
                    .show(vctx, |ui| {
                        let rect = ui.max_rect();
                        let resp = ui.interact(rect, egui::Id::new("strip_area"), egui::Sense::click());
                        ui.label(RichText::new(format!("Down {down}")).color(GREEN).size(13.0));
                        ui.label(RichText::new(format!("Up {up}")).color(BLUE).size(13.0));
                        resp.clone().on_hover_text(&hover_text);
                        resp.context_menu(|ui| {
                            ui.set_min_width(180.0);
                            let set = |a: StripAction| {
                                if let Ok(mut slot) = action_slot.lock() {
                                    *slot = Some(a);
                                }
                            };
                            if ui.button("Open FluxLimiter").clicked() {
                                set(StripAction::FocusMain);
                                ui.close_menu();
                            }
                            if ui.selectable_label(use_bits, "Show speeds in bits").clicked() {
                                set(StripAction::ToggleBits);
                                ui.close_menu();
                            }
                            if ui.button("Hide speed strip").clicked() {
                                set(StripAction::HideStrip);
                                ui.close_menu();
                            }
                            ui.separator();
                            if ui.button("Exit FluxLimiter").clicked() {
                                set(StripAction::Exit);
                                ui.close_menu();
                            }
                        });
                    });
                if vctx.input(|i| i.viewport().close_requested()) {
                    // strip is controlled from the main window checkbox
                }
            },
        );
    }
}

/// Right-click menu for an application row (table and Top
/// Applications alike), NetLimiter-style: jump to the file in
/// Explorer, open its Properties sheet, copy its path, block it.
fn app_context_menu(resp: &egui::Response, app: &AppTraffic, rules: &mut RulesStore) {
    let mut rule = rules.get_or_default(&app.exe_path, &app.name);
    let blocked = rule.blocked;
    let exe = app.exe_path.clone();
    resp.context_menu(|ui| {
        ui.set_min_width(190.0);
        if ui
            .add_enabled(
                fluxlimiter::shell_actions::AVAILABLE,
                egui::Button::new("Open file location"),
            )
            .clicked()
        {
            fluxlimiter::shell_actions::open_file_location(&exe);
            ui.close_menu();
        }
        if ui
            .add_enabled(
                fluxlimiter::shell_actions::AVAILABLE,
                egui::Button::new("Properties"),
            )
            .clicked()
        {
            fluxlimiter::shell_actions::file_properties(&exe);
            ui.close_menu();
        }
        if ui.button("Copy path").clicked() {
            ui.ctx().copy_text(exe.clone());
            ui.close_menu();
        }
        ui.separator();
        if ui.button(if blocked { "Unblock" } else { "Block" }).clicked() {
            rule.blocked = !blocked;
            rules.set(rule.clone());
            ui.close_menu();
        }
    });
}

impl eframe::App for FluxApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.poll_if_due();
        ctx.request_repaint_after(Duration::from_millis(250));
        // Drain any request from the strip's right-click menu.
        if let Some(action) = self.strip_action.lock().ok().and_then(|mut s| s.take()) {
            match action {
                StripAction::FocusMain => {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
                }
                StripAction::ToggleBits => self.use_bits = !self.use_bits,
                StripAction::HideStrip => self.show_strip = false,
                StripAction::Exit => {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                }
            }
        }
        self.taskbar_strip(ctx);

        // Load the real app icon as a texture once (same RGBA bytes
        // as the window icon); the sidebar shows it instead of a
        // hand-drawn copy of the mark.
        if self.logo.is_none() {
            let rgba = b85_decode(include_str!("../assets/icon_rgba.b85"));
            if rgba.len() == 64 * 64 * 4 {
                let image = egui::ColorImage::from_rgba_unmultiplied([64, 64], &rgba);
                self.logo = Some(ctx.load_texture(
                    "fluxlimiter-logo",
                    image,
                    egui::TextureOptions::default(),
                ));
            }
        }
        let logo = self.logo.clone();

        // ---------- sidebar ----------
        egui::SidePanel::left("sidebar")
            .exact_width(208.0)
            .frame(
                egui::Frame::none()
                    .fill(SIDEBAR_BG)
                    .inner_margin(egui::Margin { left: 10.0, right: 10.0, top: 0.0, bottom: 0.0 }),
            )
            .show(ctx, |ui| {
                ui.add_space(16.0);
                ui.horizontal(|ui| {
                    // Logo mark: the project's real icon (blue tile,
                    // three ascending white bars, from the reference
                    // design in the repo). The drawn tile below is
                    // only a fallback if the texture failed to load.
                    match &logo {
                        Some(tex) => {
                            ui.add(egui::Image::new((tex.id(), Vec2::new(34.0, 34.0))));
                        }
                        None => {
                            let (rect, _) =
                                ui.allocate_exact_size(Vec2::new(34.0, 34.0), egui::Sense::hover());
                            ui.painter().rect_filled(rect, 8.0, Color32::from_rgb(2, 124, 240));
                            let base = rect.max.y - 7.0;
                            for (i, h) in [8.0_f32, 13.0, 18.0].iter().enumerate() {
                                let bx = rect.min.x + 7.0 + i as f32 * 8.0;
                                ui.painter().rect_filled(
                                    egui::Rect::from_min_max(
                                        egui::pos2(bx, base - h),
                                        egui::pos2(bx + 5.0, base),
                                    ),
                                    1.5,
                                    Color32::WHITE,
                                );
                            }
                        }
                    }
                    ui.vertical(|ui| {
                        ui.label(RichText::new("FluxLimiter").size(16.0).strong().color(TEXT));
                        ui.label(RichText::new("Network control").color(MUTED).size(11.0));
                    });
                });
                ui.add_space(22.0);
                Self::card_label(ui, "Menu", MUTED);
                ui.add_space(6.0);
                for (i, name) in ["Dashboard", "Applications", "Rules", "Monthly Usage", "Statistics"]
                    .iter()
                    .enumerate()
                {
                    let selected = i == 0;
                    let text_color = if selected { TEXT } else { MUTED };
                    let btn = egui::Button::new(RichText::new(*name).size(13.0).color(text_color))
                        .fill(if selected { Color32::from_rgb(11, 94, 215) } else { Color32::TRANSPARENT })
                        .rounding(9.0)
                        .min_size(Vec2::new(176.0, 38.0));
                    let resp = ui.add(btn);
                    if !selected {
                        resp.on_hover_text("This page is not built yet - everything lives on the Dashboard for now.");
                    }
                    ui.add_space(4.0);
                }
                ui.add_space(18.0);
                Self::card_label(ui, "View", MUTED);
                ui.add_space(4.0);
                ui.checkbox(&mut self.show_strip, RichText::new("Taskbar speed strip").color(MUTED));
                ui.checkbox(&mut self.use_bits, RichText::new("Show speeds in bits").color(MUTED));

                // Status block pinned toward the bottom of the sidebar.
                ui.allocate_space(Vec2::new(1.0, (ui.available_height() - 118.0).max(8.0)));
                egui::Frame::none()
                    .fill(CARD)
                    .stroke(Stroke::new(1.0_f32, BORDER))
                    .rounding(10.0)
                    .inner_margin(10.0)
                    .show(ui, |ui| {
                        Self::card_label(ui, "Status", MUTED);
                        ui.add_space(4.0);
                        ui.horizontal(|ui| {
                            ui.label(RichText::new("Mode").color(MUTED));
                            ui.label(RichText::new("Standalone").color(TEXT).strong());
                        });
                        ui.horizontal(|ui| {
                            ui.label(RichText::new("Enforcement").color(MUTED));
                            ui.label(RichText::new("OFF").color(AMBER).strong());
                        });
                        ui.label(
                            RichText::new("Driver engine is the next milestone.")
                                .color(MUTED)
                                .size(11.0),
                        );
                    });
                ui.add_space(10.0);
            });

        // ---------- main column ----------
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(BG).inner_margin(18.0))
            .show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.vertical(|ui| {
                    ui.label(RichText::new("Dashboard").size(26.0).strong().color(TEXT));
                    ui.label(RichText::new("Overview of your network usage and active applications").color(MUTED));
                });
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    // Live pill: green dot + LIVE text.
                    egui::Frame::none()
                        .fill(CARD)
                        .stroke(Stroke::new(1.0_f32, BORDER))
                        .rounding(14.0)
                        .inner_margin(egui::Margin::symmetric(10.0, 5.0))
                        .show(ui, |ui| {
                            ui.horizontal(|ui| {
                                let (rect, _) = ui.allocate_exact_size(Vec2::new(8.0, 8.0), egui::Sense::hover());
                                ui.painter().circle_filled(rect.center(), 4.0, GREEN);
                                ui.label(RichText::new("LIVE").color(GREEN).size(11.0).strong());
                                ui.label(
                                    RichText::new(format!("{} apps active", self.snap.apps.len()))
                                        .color(MUTED)
                                        .size(11.0),
                                );
                            });
                        });
                });
            });
            ui.add_space(10.0);

            // Honest enforcement banner.
            egui::Frame::none()
                .fill(AMBER_BG)
                .stroke(Stroke::new(1.0_f32, AMBER))
                .rounding(10.0)
                .inner_margin(egui::Margin::symmetric(12.0, 8.0))
                .show(ui, |ui| {
                    ui.label(
                        RichText::new("Limits are saved, not enforced yet - the driver engine is the next milestone. Blocking uses Windows Firewall via the service once it is wired in.")
                            .color(AMBER)
                            .size(12.0),
                    );
                });
            ui.add_space(12.0);

            // ---- stat cards ----
            let (d_down, d_up) = self.stats.today();
            let (m_down, m_up) = self.stats.current_month();
            let month_total = m_down + m_up;
            let quota = MONTHLY_QUOTA_GB * 1024 * 1024 * 1024;
            let month_frac = month_total as f32 / quota as f32;
            ui.columns(4, |cols| {
                Self::card(&mut cols[0], |ui| {
                    ui.set_min_height(118.0);
                    Self::card_label(ui, "Download", GREEN);
                    ui.label(RichText::new(format_speed(self.snap.down_bps, self.use_bits)).size(21.0).strong().color(TEXT));
                    ui.add_space(4.0);
                    Self::sparkline(ui, &self.history, 0, GREEN);
                });
                Self::card(&mut cols[1], |ui| {
                    ui.set_min_height(118.0);
                    Self::card_label(ui, "Upload", BLUE);
                    ui.label(RichText::new(format_speed(self.snap.up_bps, self.use_bits)).size(21.0).strong().color(TEXT));
                    ui.add_space(4.0);
                    Self::sparkline(ui, &self.history, 1, BLUE);
                });
                Self::card(&mut cols[2], |ui| {
                    ui.set_min_height(118.0);
                    Self::card_label(ui, "Daily Usage", MUTED);
                    ui.label(RichText::new(format_bytes(d_down + d_up)).size(21.0).strong().color(TEXT));
                    ui.label(RichText::new(format!("Down {}   Up {}", format_bytes(d_down), format_bytes(d_up))).color(MUTED).size(11.0));
                    ui.add_space(6.0);
                    Self::progress_bar(ui, (d_down + d_up) as f32 / quota as f32, BLUE);
                    ui.label(RichText::new("Share of the monthly quota").color(MUTED).size(10.0));
                });
                Self::card(&mut cols[3], |ui| {
                    ui.set_min_height(118.0);
                    Self::card_label(ui, "Monthly Usage", MUTED);
                    ui.label(RichText::new(format!("{} / {} GB", format_bytes(month_total), MONTHLY_QUOTA_GB)).size(21.0).strong().color(TEXT));
                    ui.add_space(2.0);
                    let bar_color = if month_frac >= 0.9 { RED } else if month_frac >= 0.7 { AMBER } else { GREEN };
                    Self::progress_bar(ui, month_frac, bar_color);
                    ui.label(RichText::new(format!("{:.0}% of monthly quota", month_frac * 100.0)).color(MUTED).size(11.0));
                });
            });
            ui.add_space(12.0);

            // ---- middle row ----
            ui.columns(2, |cols| {
                Self::card(&mut cols[0], |ui| {
                    ui.set_min_height(196.0);
                    ui.label(RichText::new("Top Applications").size(14.0).strong().color(TEXT));
                    ui.label(RichText::new("Ranked by active connections").color(MUTED).size(11.0));
                    ui.add_space(8.0);
                    let top: Vec<AppTraffic> = self.snap.apps.iter().take(5).cloned().collect();
                    if top.is_empty() {
                        ui.label(RichText::new("No active connections right now.").color(MUTED));
                    }
                    let peak = top.iter().map(|a| a.connections).max().unwrap_or(1).max(1);
                    for app in top {
                        let mut name_resp: Option<egui::Response> = None;
                        ui.horizontal(|ui| {
                            name_resp = Some(ui.add_sized(
                                [150.0, 18.0],
                                egui::Label::new(&app.name).sense(egui::Sense::click()),
                            ));
                            let w = 150.0 * app.connections as f32 / peak as f32;
                            let (rect, _) = ui.allocate_exact_size(Vec2::new(160.0, 9.0), egui::Sense::hover());
                            ui.painter().rect_filled(rect, 4.0, CARD_INNER);
                            let mut fill = rect;
                            fill.max.x = rect.min.x + w.max(6.0);
                            ui.painter().rect_filled(fill, 4.0, GREEN);
                            ui.label(RichText::new(format!("{} conn", app.connections)).color(MUTED).size(11.0));
                        });
                        if let Some(resp) = &name_resp {
                            app_context_menu(resp, &app, &mut self.rules);
                        }
                        ui.add_space(3.0);
                    }
                });
                Self::card(&mut cols[1], |ui| {
                    ui.set_min_height(196.0);
                    ui.horizontal(|ui| {
                        ui.label(RichText::new("Usage - Last 7 Days").size(14.0).strong().color(TEXT));
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            ui.label(RichText::new("Upload").color(BLUE).strong().size(11.0));
                            ui.label(RichText::new("Download").color(GREEN).strong().size(11.0));
                        });
                    });
                    ui.add_space(6.0);
                    let days = self.stats.last_days(7);
                    let peak = days.iter().map(|(_, r, s)| r + s).max().unwrap_or(1).max(1) as f32;
                    let (peak_date, peak_down, peak_up) = days
                        .iter()
                        .max_by_key(|(_, r, s)| r + s)
                        .map(|(d, r, s)| (d.clone(), *r, *s))
                        .unwrap_or_default();
                    let (rect, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 138.0), egui::Sense::hover());
                    let slot = rect.width() / days.len() as f32;
                    // baseline
                    ui.painter().line_segment(
                        [egui::pos2(rect.min.x, rect.max.y - 18.0), egui::pos2(rect.max.x, rect.max.y - 18.0)],
                        Stroke::new(1.0_f32, ROW_LINE),
                    );
                    for (i, (date, r, s)) in days.iter().enumerate() {
                        let total_h = (*r + *s) as f32 / peak * 106.0;
                        let down_h = *r as f32 / peak * 106.0;
                        let cx = rect.min.x + slot * i as f32 + slot / 2.0;
                        let bw = (slot * 0.5).min(42.0);
                        let base = rect.max.y - 18.0;
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
                            egui::pos2(cx, base + 3.0),
                            egui::Align2::CENTER_TOP,
                            &date[5..],
                            egui::FontId::proportional(10.0),
                            MUTED);
                    }
                    ui.label(
                        RichText::new(format!(
                            "Peak day {}: {} down / {} up",
                            &peak_date[5..],
                            format_bytes(peak_down),
                            format_bytes(peak_up)
                        ))
                        .color(MUTED)
                        .size(11.0),
                    );
                });
            });
            ui.add_space(12.0);

            // ---- active applications table ----
            Self::card(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(RichText::new("Active Applications").size(15.0).strong().color(TEXT));
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.add(egui::TextEdit::singleline(&mut self.search).hint_text("Search applications...").desired_width(220.0));
                    });
                });
                ui.add_space(8.0);
                // column headers
                ui.horizontal(|ui| {
                    ui.add_sized([240.0, 16.0], egui::Label::new(RichText::new("APPLICATION").color(MUTED).size(10.0).strong()));
                    ui.add_sized([90.0, 16.0], egui::Label::new(RichText::new("STATUS").color(MUTED).size(10.0).strong()));
                    ui.add_sized([90.0, 16.0], egui::Label::new(RichText::new("CONNECTIONS").color(MUTED).size(10.0).strong()));
                    ui.add_sized([218.0, 16.0], egui::Label::new(RichText::new("LIMIT DOWN / UP (KB/s)").color(MUTED).size(10.0).strong()));
                    ui.add_sized([52.0, 16.0], egui::Label::new(RichText::new("RULE").color(MUTED).size(10.0).strong()));
                    ui.add_sized([64.0, 16.0], egui::Label::new(RichText::new("ACTION").color(MUTED).size(10.0).strong()));
                });
                let header_bottom = ui.cursor().min.y;
                let full = ui.available_rect_before_wrap();
                ui.painter().line_segment(
                    [egui::pos2(full.min.x, header_bottom + 2.0), egui::pos2(full.max.x, header_bottom + 2.0)],
                    Stroke::new(1.0_f32, ROW_LINE),
                );
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
                    let row_count = apps.len();
                    for (row_idx, app) in apps.into_iter().enumerate() {
                        let mut rule: Rule = self.rules.get_or_default(&app.exe_path, &app.name);
                        let mut changed = false;
                        let mut name_resp: Option<egui::Response> = None;
                        ui.horizontal(|ui| {
                            name_resp = Some(ui.add_sized(
                                [240.0, 20.0],
                                egui::Label::new(&app.name).sense(egui::Sense::click()),
                            ));
                            ui.add_sized([90.0, 20.0], egui::Label::new(RichText::new("Active").color(GREEN)));
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
                            ui.add_sized([52.0, 20.0], egui::Label::new(if rule.blocked {
                                RichText::new("Block").color(RED)
                            } else if rule.has_limit() {
                                RichText::new("Limit")
                            } else {
                                RichText::new("-").color(MUTED)
                            }));
                            if ui.add_sized([64.0, 22.0], egui::Button::new(if rule.blocked { "Unblock" } else { "Block" })).clicked() {
                                rule.blocked = !rule.blocked;
                                changed = true;
                            }
                        });
                        if changed {
                            self.rules.set(rule);
                        }
                        if let Some(resp) = &name_resp {
                            app_context_menu(resp, &app, &mut self.rules);
                        }
                        if row_idx + 1 < row_count {
                            ui.add_space(3.0);
                            let r = ui.available_rect_before_wrap();
                            let y = ui.cursor().min.y;
                            ui.painter().line_segment(
                                [egui::pos2(r.min.x, y), egui::pos2(r.max.x, y)],
                                Stroke::new(1.0_f32, ROW_LINE),
                            );
                            ui.add_space(3.0);
                        }
                    }
                });
            });
        });
    }
}
