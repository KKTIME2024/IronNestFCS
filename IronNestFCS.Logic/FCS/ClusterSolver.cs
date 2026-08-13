using System.Collections.Generic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 软目标集群解算: 直径(2R)搜索 + 最大可覆盖子集 + 最小覆盖圆(MEC)圆心作落点。
/// 纯数学, 无 IL2CPP 引用。单位: 位置=世界单位, 半径=km(ShellData.KmToWorld 转换)。
/// </summary>
public static class ClusterSolver
{
    /// <summary>候选上限: 子集枚举 2^n, 密集簇超限时取离 T 最近的几个(最优子集通常在近侧)</summary>
    private const int MaxCandidates = 8;

    /// <summary>集群结果</summary>
    public readonly struct Cluster
    {
        public readonly Vector3 Impact;   // 发射点(世界单位, MEC 圆心)
        public readonly int Count;        // 覆盖软目标数(含 T)
        public readonly float RadiusKm;   // MEC 半径(km)——诊断/边缘校验用
        public Cluster(Vector3 impact, int count, float radiusKm)
        { Impact = impact; Count = count; RadiusKm = radiusKm; }
    }

    /// <summary>
    /// 求 T 周围 2R 内的最大可覆盖软集群。
    /// 必须含 T, 子集最小覆盖圆半径 ≤ R, 且圆心不炸友军(friendlySafeKm = 杀伤包络+余量)。
    /// 无集群(只有 T)返回 null。softTargets 应为未处理的软目标世界坐标, 不含 T 自身位置。
    /// </summary>
    public static Cluster? Best(Vector3 t, List<Vector3> softTargets, float rKm, List<Vector3> friendlies, float friendlySafeKm)
    {
        float r = ShellData.KmToWorld(rKm);
        float rFriendly = ShellData.KmToWorld(friendlySafeKm);

        // 候选 = T 周围 2R(直径) 内的软目标
        var cand = new List<Vector3> { t };
        foreach (var p in softTargets)
            if ((p - t).sqrMagnitude <= 4f * r * r)
                cand.Add(p);
        if (cand.Count < 2) return null;
        if (cand.Count > MaxCandidates)
        {
            cand.Sort((a, b) => (a - t).sqrMagnitude.CompareTo((b - t).sqrMagnitude));
            cand.RemoveRange(MaxCandidates, cand.Count - MaxCandidates);
        }

        // 枚举含 T(cand[0]) 的子集, 找最大可覆盖(装进 R 圆 + 不炸友军)
        int n = cand.Count;
        int bestCount = 1;
        Vector3 bestImpact = t;
        float bestRadiusKm = 0f;
        int subsetCount = 1 << n;
        for (int mask = 1; mask < subsetCount; mask++)
        {
            if ((mask & 1) == 0) continue;   // 必须含 T
            var set = new List<Vector3>();
            for (int i = 0; i < n; i++)
                if ((mask & (1 << i)) != 0) set.Add(cand[i]);
            int count = set.Count;
            if (count <= bestCount) continue;
            if (!TryMinEnclosingCircle(set, out var center, out float radius)) continue;
            if (radius > r + 1e-4f) continue;                    // 装不进爆圆
            if (HasFriendlyNear(center, rFriendly, friendlies)) continue; // 友军禁区(杀伤包络+余量)
            bestCount = count;
            bestImpact = center;
            bestRadiusKm = radius * ShellData.KmPerWorldUnit;
        }

        return bestCount >= 2 ? new Cluster(bestImpact, bestCount, bestRadiusKm) : null;
    }

    private static bool HasFriendlyNear(Vector3 center, float r, List<Vector3> friendlies)
    {
        foreach (var f in friendlies)
            if ((f - center).sqrMagnitude <= r * r) return true;
        return false;
    }

    // ─── 最小包围圆(小规模暴力): 圆由 1 点/2 点(直径)/3 点(外接圆)决定 ───

    private static bool TryMinEnclosingCircle(List<Vector3> pts, out Vector3 center, out float radius)
    {
        center = pts[0];
        radius = float.MaxValue;
        int n = pts.Count;

        for (int i = 0; i < n; i++)
            if (ContainsAll(pts, pts[i], 0f, radius)) { center = pts[i]; radius = 0f; }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var c = (pts[i] + pts[j]) * 0.5f;
                float r = (pts[i] - pts[j]).magnitude * 0.5f;
                if (ContainsAll(pts, c, r, radius)) { center = c; radius = r; }
            }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                for (int k = j + 1; k < n; k++)
                    if (Circumcenter(pts[i], pts[j], pts[k], out var c, out float r))
                        if (ContainsAll(pts, c, r, radius)) { center = c; radius = r; }

        return radius < float.MaxValue;
    }

    private static bool ContainsAll(List<Vector3> pts, Vector3 c, float r, float bestR)
    {
        if (r >= bestR) return false;   // 非更小圆, 跳过
        float tol = r + 1e-4f;
        float tolSq = tol * tol;
        foreach (var p in pts)
            if ((p - c).sqrMagnitude > tolSq) return false;
        return true;
    }

    /// <summary>三点外接圆圆心+半径(3D, 共面点有效)。共线返回 false。</summary>
    private static bool Circumcenter(Vector3 a, Vector3 b, Vector3 c, out Vector3 center, out float radius)
    {
        center = Vector3.zero;
        radius = 0f;
        var A = b - a;
        var B = c - a;
        var N = Vector3.Cross(A, B);
        float n2 = N.sqrMagnitude;
        if (n2 < 1e-8f) return false;   // 共线, 用直径圆(2 点)覆盖
        var crossA = Vector3.Cross(B, N);
        var crossB = Vector3.Cross(N, A);
        center = a + (Vector3.Dot(A, A) * crossA + Vector3.Dot(B, B) * crossB) / (2f * n2);
        radius = (center - a).magnitude;
        return true;
    }
}
