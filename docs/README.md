# 文档索引

IronNestFCS-Automat 的文档地图。分支约定：**master = 稳定线**（v1.5.1 起，反炮兵停用），**dev = 反炮兵实验修复线**。

## 设计与玩法

| 文档 | 内容 |
| --- | --- |
| [cbt-mode.md](cbt-mode.md) | 无尽反炮兵模式：经济运营与生存策略（实验模式，偶发异常原因未知；稳定版停用，开发在 dev 分支） |
| [cluster-strike.md](cluster-strike.md) | 集群打击：HE/HCHE 一发多杀与最小外接圆（MEC） |
| [moving-target-tracking.md](moving-target-tracking.md) | 移动目标跟踪：冻结快照 + 匀速外推提前量 |
| [feasibility-report.md](feasibility-report.md) | 可行性调研报告（早期） |

## 设计规格（specs）

| 文档 | 内容 |
| --- | --- |
| [specs/2026-08-13-map-overlay-visual-design.md](superpowers/specs/2026-08-13-map-overlay-visual-design.md) | 地图 overlay v2 视觉设计（1930 手绘风格，已实现） |
| [specs/2026-08-07-auto-mode-switch-gamepad-design.md](superpowers/specs/2026-08-07-auto-mode-switch-gamepad-design.md) | 全自动/手动切换与手柄支持设计 |
| [specs/2026-08-16-firing-coordination-redesign.md](superpowers/specs/2026-08-16-firing-coordination-redesign.md) | 击发协调重设计（草案，⚠️ 未决项，尚未实施） |

## 计划（plans）

| 文档 | 内容 |
| --- | --- |
| [plans/2026-08-07-auto-mode-switch-gamepad.md](superpowers/plans/2026-08-07-auto-mode-switch-gamepad.md) | 自动模式切换实施计划 |
| [plans/2026-08-10-coop-mode.md](superpowers/plans/2026-08-10-coop-mode.md) | 双人协作模式计划 |
| [plans/2026-08-10-human-machine-collaboration-registry.md](superpowers/plans/2026-08-10-human-machine-collaboration-registry.md) | 人机协作注册表计划 |
| [plans/2026-08-12-moving-target-tracking.md](superpowers/plans/2026-08-12-moving-target-tracking.md) | 移动目标跟踪实施计划 |

## 开发日志

- [DEVLOG.md](../DEVLOG.md) — 总开发日志
- [daily/](daily/) — 每日开发日志（2026-08-02 / 2026-08-12）

## 发布

- Release 目录（`Release/`，gitignore）存放发布 zip 与对应 release notes：
  - v1.5.1 稳定版（CBT 停用）
  - v1.5.0 pre-release（含反炮兵实验模式）
  - v1.4.x 及更早
- 发布说明全文见各版本的 `*_release_notes.md`
