## ============================================================
## CUBIA benchmark: Results-section figures and Table 2 values for the paper
##
## Same replication model as CompoundMetrics.R:
##   frames -> window medians -> one value per session -> mean +/- SD over sessions
##
## Writes to <data_dir>/paper_labels/
##   fig_scaling.pdf      full width (\textwidth, 15.8 cm): (a) delivered frame time,
##                        (b) GPU time per frame, vs point count, log-log
##   fig_bottleneck.pdf   one column (7.5 cm): GPU vs CPU main/render thread at 1M points
##   fig_rebuild.pdf      one column (7.5 cm): warm rebuild time vs point count
##   tab_capacity.tex     LaTeX table: 90 Hz capacity, GPU cost per point, rebuild rate
##   (+ PNG previews of each figure)
##
## Style: Times to match the template, 8-9 pt text at final size, no in-figure titles
## (the LaTeX caption carries the message). Pipelines keep one colour AND one marker
## shape everywhere, so the figures survive greyscale printing.
##
## Usage
##   RStudio: open CUBIA_benchmarks.Rproj and Source this file
##   Rscript: Rscript paper_figures.R <folder-with-session-CSVs>   (default: results/)
## ============================================================

suppressPackageStartupMessages({
  library(dplyr); library(tidyr); library(readr); library(stringr); library(purrr)
  library(ggplot2); library(scales); library(patchwork)
})

# ---- 0. Files ------------------------------------------------------------
args <- commandArgs(trailingOnly = TRUE)
data_dir <- if (length(args) > 0) args[1] else "results"
if (!dir.exists(data_dir)) stop("Folder not found: ", normalizePath(data_dir, mustWork = FALSE))
csv_paths <- list.files(data_dir, pattern = "^benchmark_results_(IATK_)?standalone_s\\d+_.*\\.csv$", full.names = TRUE)
if (length(csv_paths) == 0) stop("No session CSVs in ", data_dir)
out_dir <- file.path(data_dir, "paper_labels")
dir.create(out_dir, showWarnings = FALSE)

read_session <- function(path) {
  read_csv(path, show_col_types = FALSE,
           col_types = cols(Stage = col_character(), Flags = col_character(),
                            Timestamp = col_character(),
                            MemBeforeBytes = col_double(), MemAfterBytes = col_double(),
                            BuildTimeMs = col_double(), CpuMainMs = col_double(),
                            CpuRenderMs = col_double(), .default = col_guess())) %>%
    mutate(Session = str_match(basename(path), "_standalone_([^_]+)_")[, 2])
}
df <- map_dfr(csv_paths, read_session)

pipeline_levels <- c("Instanced", "Particle", "VFX", "IATK")
# Technique labels used in the paper (Sec. 4.4); component names in brackets in the text only.
pipeline_labels <- c(Instanced = "Instanced Mesh", Particle = "CPU Particles",
                     VFX = "GPU Particles", IATK = "Single Mesh (IATK)")
budget90 <- 1000 / 90
miss_tol <- 1.05
gpu_floor_ms <- 3

# ---- 1. Frames -> windows -> sessions (identical to CompoundMetrics.R) ------------
frame_df <- df %>%
  filter(RowType == "frame", GpuTimeMs > 0,
         is.na(Flags) | !str_detect(Flags, "nopresence")) %>%
  mutate(Pipeline = factor(Pipeline, levels = pipeline_levels))

frames_per_window <- frame_df %>% count(Session, Pipeline, PointCount, Window) %>% pull(n) %>% max()

win <- frame_df %>%
  group_by(Session, Pipeline, PointCount, Window) %>%
  summarise(n = n(), gpu = median(GpuTimeMs), frame = median(FrameTimeMs),
            main = median(CpuMainMs), render = median(CpuRenderMs), .groups = "drop") %>%
  mutate(complete = n >= 0.98 * frames_per_window)

timed_out <- df %>% filter(RowType == "timeout") %>% distinct(Session, Pipeline, PointCount) %>%
  mutate(Pipeline = factor(Pipeline, levels = pipeline_levels), timed_out = TRUE)

sess <- win %>%
  group_by(Session, Pipeline, PointCount) %>%
  summarise(windows = sum(complete), partial_only = !any(complete),
            across(c(gpu, frame, main, render),
                   ~ if (any(complete)) median(.x[complete]) else median(.x)),
            .groups = "drop") %>%
  left_join(timed_out, by = c("Session", "Pipeline", "PointCount")) %>%
  mutate(timed_out = coalesce(timed_out, FALSE))

# Condition = mean +/- SD over sessions. "incomplete" = some session timed out or
# had no complete 900-frame window; plotted with hollow markers.
cond <- sess %>%
  group_by(Pipeline, PointCount) %>%
  summarise(across(c(gpu, frame, main, render), list(m = mean, s = sd)),
            incomplete = any(timed_out | partial_only), .groups = "drop")

build_sess <- df %>% filter(RowType == "build", Window >= 1) %>%     # repeat 0 = cold start
  mutate(Pipeline = factor(Pipeline, levels = pipeline_levels)) %>%
  group_by(Session, Pipeline, PointCount) %>%
  summarise(build_ms = median(BuildTimeMs), .groups = "drop")
build_cond <- build_sess %>% group_by(Pipeline, PointCount) %>%
  summarise(m = mean(build_ms), s = sd(build_ms), .groups = "drop")

# ---- 2. Shared style ------------------------------------------------------------
# Validated categorical slots (CVD-safe across all pairs); aqua is < 3:1 on white,
# so every series is also direct-labelled and has its own marker shape.
pipe_col   <- c(Instanced = "#eb6834", Particle = "#2a78d6", VFX = "#1baf7a", IATK = "#5b3fa8")
pipe_shape <- c(Instanced = 21, Particle = 24, VFX = 22, IATK = 23) # circle, triangle, square, diamond
ink <- "#0b0b0b"; ink2 <- "#52514e"; grid_col <- "#e6e6e3"

base_pt <- 8
theme_paper <- function() {
  theme_minimal(base_size = base_pt, base_family = "Times") +
    theme(text = element_text(colour = ink),
          axis.text = element_text(colour = ink2, size = base_pt - 1),
          axis.title = element_text(size = base_pt),
          panel.grid.minor = element_blank(),
          panel.grid.major = element_line(colour = grid_col, linewidth = 0.25),
          legend.position = "top", legend.title = element_blank(),
          legend.text = element_text(size = base_pt), legend.margin = margin(0, 0, 0, 0),
          legend.key.width = unit(0.45, "cm"), legend.key.height = unit(0.3, "cm"),
          legend.spacing.x = unit(3, "pt"),
          legend.box.margin = margin(0, 0, -4, 0),
          plot.tag = element_text(size = base_pt + 1, face = "bold"),
          plot.margin = margin(3, 4, 3, 3))
}
lab_points <- label_number(scale_cut = cut_short_scale())        # 10K, 100K, 1M, 10M
x_points <- scale_x_log10(breaks = 10^(4:7), labels = lab_points,
                          expand = expansion(mult = c(0.06, 0.06)))
y_ms_log <- function(...) scale_y_log10(labels = label_number(accuracy = 1, big.mark = ","),
                                        breaks = 10^(0:4), ...)
# side = "right" puts the label at the 10M end, "left" at the 10K end (below the line).
budget_line <- function(label = "90 Hz budget (11.1 ms)", side = "right") list(
  geom_hline(yintercept = budget90, linetype = "22", linewidth = 0.35, colour = ink2),
  annotate("text", x = if (side == "right") 1.15e7 else 0.87e4, y = budget90 / 1.22, label = label,
           hjust = if (side == "right") 1 else 0, vjust = 1,
           size = (base_pt - 1) / .pt, family = "Times", colour = ink2))

series_layers <- function(d, ycol, scol) list(
  geom_line(aes(colour = Pipeline), linewidth = 0.6),
  geom_errorbar(aes(ymin = pmax(.data[[ycol]] - .data[[scol]], 1e-3),
                    ymax = .data[[ycol]] + .data[[scol]], colour = Pipeline),
                width = 0.08, linewidth = 0.35),
  geom_point(data = filter(d, !incomplete), aes(fill = Pipeline, shape = Pipeline),
             colour = "white", size = 1.9, stroke = 0.3),
  geom_point(data = filter(d, incomplete), aes(colour = Pipeline, shape = Pipeline),
             fill = "white", size = 1.7, stroke = 0.6, show.legend = FALSE),
  scale_colour_manual(values = pipe_col, labels = pipeline_labels, drop = FALSE),
  scale_fill_manual(values = pipe_col, labels = pipeline_labels, drop = FALSE),
  scale_shape_manual(values = pipe_shape, labels = pipeline_labels, drop = FALSE))

save_fig <- function(p, name, w_cm, h_cm) {
  ggsave(file.path(out_dir, paste0(name, ".pdf")), p, width = w_cm, height = h_cm,
         units = "cm", device = if (capabilities("cairo")) cairo_pdf else "pdf")   # cairo embeds fonts
  ggsave(file.path(out_dir, paste0(name, ".png")), p, width = w_cm, height = h_cm,
         units = "cm", dpi = 300, bg = "white")
}

# ---- 3. Figure: scaling (full width) --------------------------------------------------
scaling_panel <- function(ycol, scol, ylab, tag, side = "right") {
  d <- cond %>% mutate(y = .data[[ycol]])
  ggplot(d, aes(PointCount, .data[[ycol]], group = Pipeline)) +
    budget_line(side = side) +
    series_layers(d, ycol, scol) +
    x_points + y_ms_log() + coord_cartesian(ylim = c(0.9, 4000)) +
    labs(x = "Points", y = ylab, tag = tag) + theme_paper()
}
p_frame <- scaling_panel("frame_m", "frame_s", "Delivered frame time (ms)", "(a)")
p_gpu   <- scaling_panel("gpu_m",   "gpu_s",   "GPU time per frame (ms)",   "(b)", side = "left")
p_scaling <- (p_frame | p_gpu) + plot_layout(guides = "collect") &
  theme(legend.position = "top")
save_fig(p_scaling, "fig_scaling", 15.8, 6.2)

# ---- 4. Figure: bottleneck at 1M points (one column) ----------------------------------
# Instanced is left out: ~2.6 s per frame at 1M would flatten the other two (in caption).
comp_levels <- c(gpu = "GPU", main = "CPU main", render = "CPU render")
comp_fill   <- c("GPU" = "#3d3c3a", "CPU main" = "#8d8c87", "CPU render" = "#c9c8c2")
bn <- cond %>% filter(PointCount == 1e6, Pipeline != "Instanced") %>%
  select(Pipeline, frame_m, gpu_m, gpu_s, main_m, main_s, render_m, render_s) %>%
  pivot_longer(c(gpu_m, main_m, render_m), names_to = "comp", values_to = "ms") %>%
  mutate(sd = case_when(comp == "gpu_m" ~ gpu_s, comp == "main_m" ~ main_s, TRUE ~ render_s),
         comp = factor(comp_levels[str_remove(comp, "_m")], levels = rev(comp_levels)),
         Pipeline = factor(pipeline_labels[as.character(Pipeline)],
                           levels = rev(pipeline_labels[c("Particle", "VFX", "IATK")])))
bn_frame <- bn %>% distinct(Pipeline, frame_m)
dodge_off <- setNames(c(-1, 0, 1) * 0.82 / 3, levels(bn$comp))       # render, main, GPU
bn_lab <- bn %>% mutate(y = as.numeric(Pipeline) + dodge_off[as.character(comp)],
                        inside = ms >= 9)

p_bn <- ggplot(bn, aes(ms, Pipeline)) +
  geom_vline(xintercept = budget90, linetype = "22", linewidth = 0.35, colour = ink2) +
  geom_col(aes(fill = comp), position = position_dodge(width = 0.82), width = 0.74) +
  geom_errorbarh(aes(xmin = pmax(ms - sd, 0), xmax = ms + sd, group = comp),
                 position = position_dodge(width = 0.82), height = 0.18,
                 linewidth = 0.3, colour = ink) +
  # Value labels at explicit y positions (dodge offsets for 3 bars in width 0.82):
  # long bars carry the value inside at their left end, short bars just outside.
  geom_text(data = bn_lab %>% filter(inside),
            aes(x = 0.7, y = y, label = sprintf("%.1f", ms), colour = comp == "CPU render"),
            hjust = 0, size = (base_pt - 1) / .pt, family = "Times", show.legend = FALSE) +
  geom_text(data = bn_lab %>% filter(!inside),
            aes(x = ms + sd + 0.6, y = y, label = sprintf("%.1f", ms)),
            hjust = 0, size = (base_pt - 1) / .pt, family = "Times", colour = ink) +
  scale_colour_manual(values = c(`TRUE` = ink, `FALSE` = "white"), guide = "none") +
  geom_segment(data = bn_frame, aes(x = frame_m, xend = frame_m,
                                     y = as.numeric(Pipeline) - 0.42, yend = as.numeric(Pipeline) + 0.42),
               linewidth = 0.6, colour = ink) +
  geom_text(data = bn_frame, aes(frame_m, Pipeline, label = sprintf("frame\n%.1f", frame_m)),
            hjust = -0.15, lineheight = 0.9, size = (base_pt - 1) / .pt, family = "Times", colour = ink) +
  scale_fill_manual(values = comp_fill, breaks = comp_levels) +
  scale_x_continuous(expand = expansion(mult = c(0, 0)), limits = c(0, 52),
                     breaks = seq(0, 50, 10)) +
  coord_cartesian(clip = "off") +
  labs(x = "Milliseconds per frame at 1M points", y = NULL) +
  theme_paper() + theme(panel.grid.major.y = element_blank(),
                        axis.text.y = element_text(colour = ink, size = base_pt),
                        legend.justification = "right")  # keep the legend inside the figure
save_fig(p_bn, "fig_bottleneck", 7.5, 7.0)

# ---- 5. Figure: warm rebuild time (one column) ------------------------------------------
rb <- build_cond %>% mutate(incomplete = FALSE)
p_rb <- ggplot(rb, aes(PointCount, m, group = Pipeline)) +
  budget_line("one 90 Hz frame (11.1 ms)") +
  series_layers(rb, "m", "s") +
  x_points + y_ms_log() +
  labs(x = "Points", y = "Warm rebuild time (ms)") +
  theme_paper() + guides(colour = guide_legend(nrow = 2, byrow = TRUE),
                          fill = guide_legend(nrow = 2, byrow = TRUE),
                          shape = guide_legend(nrow = 2, byrow = TRUE))
save_fig(p_rb, "fig_rebuild", 7.5, 6.0)

# ---- 6. Table: capacity, GPU cost, rebuild rate ------------------------------------------
interp_capacity <- function(N, y, budget) {
  o <- order(N); N <- N[o]; y <- y[o]
  if (y[1] > budget) return(NA_real_)
  k <- max(which(y <= budget))
  if (k == length(N)) return(Inf)
  N[k] + (budget - y[k]) * (N[k + 1] - N[k]) / (y[k + 1] - y[k])
}
cap <- sess %>% group_by(Session, Pipeline) %>%
  summarise(cap_frame = interp_capacity(PointCount,
                                        ifelse(frame <= miss_tol * budget90,
                                               pmax(gpu, main, render), frame), budget90),
            cap_gpu = interp_capacity(PointCount, gpu, budget90), .groups = "drop")

fit_tiers <- sess %>% group_by(Pipeline, PointCount) %>%
  summarise(gpu_mean = mean(gpu), partial = all(partial_only), .groups = "drop") %>%
  filter(gpu_mean >= gpu_floor_ms, !partial) %>% select(Pipeline, PointCount)
gpu_cost <- sess %>% semi_join(fit_tiers, by = c("Pipeline", "PointCount")) %>%
  group_by(Session, Pipeline) %>%
  summarise(ns = coef(lm(gpu ~ PointCount))[2] * 1e6, .groups = "drop")
rebuild_rate <- build_sess %>% group_by(Session, Pipeline) %>%
  summarise(ms_per_M = coef(lm(build_ms ~ PointCount))[2] * 1e6, .groups = "drop")

fmt_k <- function(x) if (all(is.na(x))) "$<$10k" else sprintf("%.0fk", mean(x) / 1e3)
fmt_v <- function(x, d) sprintf(paste0("%.", d, "f $\\pm$ %.", d, "f"), mean(x), sd(x))
tab <- tibble(Pipeline = factor(pipeline_levels, levels = pipeline_levels)) %>%
  left_join(cap %>% group_by(Pipeline) %>%
              summarise(cap_frame = fmt_k(cap_frame), cap_gpu = fmt_k(cap_gpu)), by = "Pipeline") %>%
  left_join(gpu_cost %>% group_by(Pipeline) %>% summarise(ns = fmt_v(ns, if (mean(ns) >= 100) 0 else 1)), by = "Pipeline") %>%
  left_join(rebuild_rate %>% group_by(Pipeline) %>% summarise(rb = fmt_v(ms_per_M, 0)), by = "Pipeline")

tex <- c(
  "\\begin{table}[t]",
  "  \\caption{Largest point count sustaining 90\\,Hz (whole frame and GPU time alone, interpolated linearly between measured tiers), GPU cost per added point and warm rebuild rate for our three back-ends and IATK. Means over three sessions; $\\pm$ gives the SD.}",
  "  \\label{tab:capacity}",
  "  \\centering",
  "  \\footnotesize",
  "  \\setlength{\\tabcolsep}{3pt}",
  "  \\begin{tabular}{@{}lcccc@{}}",
  "    \\hline",
  "     & \\multicolumn{2}{c}{Points at 90\\,Hz} & GPU & Rebuild \\\\",
  "     & frame & GPU only & ns/point & ms/M pts \\\\",
  "    \\hline",
  sprintf("    %s & %s & %s & %s & %s \\\\", pipeline_labels[as.character(tab$Pipeline)],
          tab$cap_frame, tab$cap_gpu, tab$ns, tab$rb),
  "    \\hline",
  "  \\end{tabular}",
  "\\end{table}")
writeLines(tex, file.path(out_dir, "tab_capacity.tex"))

# ---- 7. Numbers quoted in the text --------------------------------------------------
options(width = 160)
cat("\nCondition means (ms):\n")
print(as.data.frame(cond %>% mutate(across(where(is.numeric), ~ round(.x, 2)))), row.names = FALSE)
cat("\nWarm rebuild (ms):\n"); print(as.data.frame(build_cond %>% mutate(across(c(m, s), ~ round(.x, 1)))), row.names = FALSE)
cat("\nTable:\n"); writeLines(tex)
cat("\nWrote figures and table to ", normalizePath(out_dir), "\n", sep = "")
