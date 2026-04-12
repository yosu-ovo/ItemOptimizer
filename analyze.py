#!/usr/bin/env python3
"""
ItemOptimizer 统一性能分析脚本
用法:
  python analyze.py snapshot <path>                      # 分析 snapshot JSON
  python analyze.py record <path>                        # 分析 record CSV
  python analyze.py compare <snapshot.json> <record.csv> # 交叉对比
"""
import sys
import json
import csv
from collections import defaultdict
from pathlib import Path


def load_snapshot(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def load_record(path: str) -> list[dict]:
    rows = []
    with open(path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            rows.append({
                "frame": int(row["frame"]),
                "identifier": row["identifier"],
                "package": row["package"],
                "time_ms": float(row["time_ms"]),
                "update_count": int(row["update_count"]),
            })
    return rows


# ─── snapshot ───────────────────────────────────────────

def cmd_snapshot(path: str):
    data = load_snapshot(path)
    print(f"=== Snapshot Analysis: {Path(path).name} ===")
    print(f"Timestamp: {data.get('timestamp', '?')}")
    print(f"Game Version: {data.get('gameVersion', '?')}")
    print()

    # Enabled packages ranked by item prefab count
    pkgs = data.get("enabledPackages", [])
    pkgs_sorted = sorted(pkgs, key=lambda p: p.get("itemPrefabCount", 0), reverse=True)
    print(f"─── Enabled Packages ({len(pkgs)}) by prefab count ───")
    print(f"  {'Package':<45s} {'Prefabs':>8s}  {'Workshop ID':>14s}")
    for p in pkgs_sorted[:30]:
        print(f"  {p['name']:<45s} {p.get('itemPrefabCount',0):>8d}  {p.get('workshopId','?'):>14s}")
    print()

    # Instantiated items top 30
    instances = data.get("instantiatedItems", [])
    print(f"─── Instantiated Items (top 30 by count) ───")
    print(f"  {'Identifier':<35s} {'Package':<30s} {'Total':>6s} {'Active':>7s}")
    for item in instances[:30]:
        print(f"  {item['identifier']:<35s} {item['package']:<30s} {item['total']:>6d} {item['active']:>7d}")
    print()

    # Frame perf
    perf = data.get("framePerf", {})
    loop_ms = perf.get("loopTotalMs", 0)
    items = perf.get("items", [])
    print(f"─── Single-Frame Performance (loop total: {loop_ms:.2f}ms) ───")
    print(f"  {'Identifier':<35s} {'Package':<30s} {'Time(ms)':>10s} {'Count':>6s}")
    for item in items[:20]:
        print(f"  {item['identifier']:<35s} {item['package']:<30s} {item['timeMs']:>10.4f} {item['count']:>6d}")
    print()

    # Aggregate by package
    pkg_time = defaultdict(float)
    for item in items:
        pkg_time[item["package"]] += item["timeMs"]
    pkg_sorted = sorted(pkg_time.items(), key=lambda x: x[1], reverse=True)
    print(f"─── Per-Package Frame Cost (top 15) ───")
    print(f"  {'Package':<45s} {'Time(ms)':>10s} {'%':>7s}")
    total_time = sum(t for _, t in pkg_sorted) or 1
    for pkg, t in pkg_sorted[:15]:
        print(f"  {pkg:<45s} {t:>10.4f} {t/total_time*100:>6.1f}%")
    print()

    # Prefab complexity
    prefabs = data.get("itemPrefabs", [])
    pkg_complexity = defaultdict(lambda: {"count": 0, "total_comp": 0, "total_se": 0})
    for pf in prefabs:
        pkg = pf.get("package", "Unknown")
        d = pkg_complexity[pkg]
        d["count"] += 1
        d["total_comp"] += pf.get("compCount", 0)
        d["total_se"] += pf.get("seCount", 0)
    print(f"─── Prefab Complexity by Package (top 15) ───")
    print(f"  {'Package':<45s} {'Prefabs':>8s} {'Avg Comp':>9s} {'Avg SE':>8s}")
    for pkg, d in sorted(pkg_complexity.items(), key=lambda x: x[1]["total_se"], reverse=True)[:15]:
        avg_c = d["total_comp"] / max(d["count"], 1)
        avg_s = d["total_se"] / max(d["count"], 1)
        print(f"  {pkg:<45s} {d['count']:>8d} {avg_c:>9.1f} {avg_s:>8.1f}")


# ─── record ─────────────────────────────────────────────

def cmd_record(path: str):
    rows = load_record(path)
    if not rows:
        print("No data in record file.")
        return

    n_frames = max(r["frame"] for r in rows)
    print(f"=== Record Analysis: {Path(path).name} ({n_frames} frames) ===")
    print()

    # Per-identifier stats
    ident_stats = defaultdict(lambda: {"times": [], "counts": [], "pkg": "?"})
    for r in rows:
        d = ident_stats[r["identifier"]]
        d["times"].append(r["time_ms"])
        d["counts"].append(r["update_count"])
        d["pkg"] = r["package"]

    results = []
    for ident, d in ident_stats.items():
        times = sorted(d["times"])
        n = len(times)
        total = sum(times)
        mean = total / n
        p50 = times[n // 2]
        p95 = times[int(n * 0.95)]
        mx = times[-1]
        avg_count = sum(d["counts"]) / n
        cv = (sum((t - mean)**2 for t in times) / n) ** 0.5 / mean if mean > 0 else 0
        results.append({
            "identifier": ident, "package": d["pkg"],
            "mean": mean, "p50": p50, "p95": p95, "max": mx,
            "total": total, "avg_count": avg_count, "cv": cv,
        })

    # Top items by mean time
    results.sort(key=lambda x: x["mean"], reverse=True)
    print(f"─── Top 20 Items by Mean Time ───")
    print(f"  {'Identifier':<35s} {'Package':<25s} {'Mean':>8s} {'P50':>8s} {'P95':>8s} {'Max':>8s} {'Cnt':>5s} {'CV':>5s}")
    for r in results[:20]:
        print(f"  {r['identifier']:<35s} {r['package']:<25s} "
              f"{r['mean']:>8.4f} {r['p50']:>8.4f} {r['p95']:>8.4f} {r['max']:>8.4f} "
              f"{r['avg_count']:>5.0f} {r['cv']:>5.2f}")
    print()

    # Aggregate by package
    pkg_agg = defaultdict(lambda: {"total_mean": 0, "item_count": 0})
    for r in results:
        d = pkg_agg[r["package"]]
        d["total_mean"] += r["mean"]
        d["item_count"] += 1
    pkg_sorted = sorted(pkg_agg.items(), key=lambda x: x[1]["total_mean"], reverse=True)

    total_all = sum(d["total_mean"] for _, d in pkg_sorted) or 1
    print(f"─── Top 10 Packages by Total Time ───")
    print(f"  {'Package':<45s} {'Total(ms)':>10s} {'Items':>6s} {'%':>7s}")
    for pkg, d in pkg_sorted[:10]:
        print(f"  {pkg:<45s} {d['total_mean']:>10.4f} {d['item_count']:>6d} {d['total_mean']/total_all*100:>6.1f}%")
    print()

    # Frame-level overview
    frame_totals = defaultdict(float)
    for r in rows:
        frame_totals[r["frame"]] += r["time_ms"]
    frame_vals = sorted(frame_totals.values())
    n = len(frame_vals)
    if n > 0:
        avg_frame = sum(frame_vals) / n
        p95_frame = frame_vals[int(n * 0.95)]
        max_frame = frame_vals[-1]
        print(f"─── Frame-Level Summary ───")
        print(f"  Avg frame item time: {avg_frame:.2f}ms")
        print(f"  P95 frame item time: {p95_frame:.2f}ms")
        print(f"  Max frame item time: {max_frame:.2f}ms")


# ─── compare ────────────────────────────────────────────

def cmd_compare(snap_path: str, rec_path: str):
    data = load_snapshot(snap_path)
    rows = load_record(rec_path)

    print(f"=== Cross-Reference: {Path(snap_path).name} + {Path(rec_path).name} ===")
    print()

    # Instance counts from snapshot
    inst_map = {}
    for item in data.get("instantiatedItems", []):
        inst_map[item["identifier"]] = item

    # Runtime costs from recording
    ident_stats = defaultdict(lambda: {"times": [], "pkg": "?"})
    for r in rows:
        ident_stats[r["identifier"]]["times"].append(r["time_ms"])
        ident_stats[r["identifier"]]["pkg"] = r["package"]

    # Aggregate per-package
    pkg_data = defaultdict(lambda: {
        "total_instances": 0, "active_instances": 0,
        "total_runtime_ms": 0, "item_count": 0
    })

    for ident, d in ident_stats.items():
        mean_ms = sum(d["times"]) / len(d["times"])
        inst = inst_map.get(ident, {"total": 0, "active": 0})
        pkg = d["pkg"]
        pd = pkg_data[pkg]
        pd["total_instances"] += inst.get("total", 0)
        pd["active_instances"] += inst.get("active", 0)
        pd["total_runtime_ms"] += mean_ms
        pd["item_count"] += 1

    pkg_sorted = sorted(pkg_data.items(), key=lambda x: x[1]["total_runtime_ms"], reverse=True)

    print(f"─── Package Ranking: Instances vs Runtime ───")
    print(f"  {'Package':<40s} {'Items':>6s} {'Active':>7s} {'Runtime(ms)':>12s} {'ms/active':>10s}")
    for pkg, d in pkg_sorted[:15]:
        ms_per = d["total_runtime_ms"] / max(d["active_instances"], 1)
        print(f"  {pkg:<40s} {d['total_instances']:>6d} {d['active_instances']:>7d} "
              f"{d['total_runtime_ms']:>12.4f} {ms_per:>10.4f}")
    print()

    # Top items: sorted by mean runtime, enriched with instance data
    item_results = []
    for ident, d in ident_stats.items():
        mean_ms = sum(d["times"]) / len(d["times"])
        inst = inst_map.get(ident, {"total": 0, "active": 0})
        item_results.append({
            "identifier": ident, "package": d["pkg"],
            "mean_ms": mean_ms,
            "total": inst.get("total", 0), "active": inst.get("active", 0),
        })
    item_results.sort(key=lambda x: x["mean_ms"], reverse=True)

    print(f"─── Top 20 Items: Runtime + Instance Detail ───")
    print(f"  {'Identifier':<35s} {'Package':<25s} {'Mean(ms)':>9s} {'Total':>6s} {'Active':>7s}")
    for r in item_results[:20]:
        print(f"  {r['identifier']:<35s} {r['package']:<25s} "
              f"{r['mean_ms']:>9.4f} {r['total']:>6d} {r['active']:>7d}")


# ─── main ───────────────────────────────────────────────

def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    cmd = sys.argv[1].lower()

    if cmd == "snapshot" and len(sys.argv) >= 3:
        cmd_snapshot(sys.argv[2])
    elif cmd == "record" and len(sys.argv) >= 3:
        cmd_record(sys.argv[2])
    elif cmd == "compare" and len(sys.argv) >= 4:
        cmd_compare(sys.argv[2], sys.argv[3])
    else:
        print(__doc__)
        sys.exit(1)


if __name__ == "__main__":
    main()
