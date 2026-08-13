using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    private Transform? turret;
    private Dictionary<int, Transform> artilleries;
    private Transform? fireMissionRoot;
    private FireMission? fireMission;
    private Transform? mapSurface;
    /// <summary>真实炮塔: MapRoot 下的固定子物体, 玩家拖动不移动它</summary>
    private Transform? turretLocation;

    /// <summary>炮塔 Transform 只读访问(供 TacticalRadar 等使用)</summary>
    public Transform? Turret => turret;

    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        var turretObject = GameObject.Find("Player Turret Piece");
        if (turretObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Player Turret Piece，当前场景尚未就绪");
            return false;
        }

        var mapObject = GameObject.Find("Draggable Surface");
        if (mapObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Draggable Surface，当前场景尚未就绪");
            return false;
        }

        turret = turretObject.transform;
        var turretLocObject = GameObject.Find("TurretLocation");
        if (turretLocObject != null) {
            turretLocation = turretLocObject.transform;
        } else {
            MelonLogger.Warning("[FCS] 未找到 TurretLocation，回退 Player Turret Piece");
        }
        mapSurface = mapObject.transform;
        var map = mapObject.transform;
        for (var i = 0; i < map.childCount; ++i) {
            var t = map.GetChild(i);
            if (t.name != "MapToken_Artillery") continue;
            var tmp = t.GetComponentInChildren<Il2CppTMPro.TextMeshPro>();
            if (tmp == null) continue;
            if (!int.TryParse(tmp.text, out var id)) continue;
            artilleries.Add(id, t);
        }
        MelonLogger.Msg($"[FCS] 找到 Player Turret Piece: {turret}, Artilleries: {artilleries.Count}");
        var fireMissionObject = GameObject.Find("Fire Mission Root");
        if (fireMissionObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Fire Mission Root，当前场景尚未就绪");
            return false;
        }

        fireMissionRoot = fireMissionObject.transform;
        fireMission = fireMissionRoot.GetComponent<FireMission>();
        return fireMission != null;
    }

    /// <summary>真实炮塔坐标 → 地图局部坐标。
    /// TurretLocation 是 MapRoot 下的固定子物体（玩家拖动不移动），
    /// Player Turret Piece 是可拖动标记，仅作回退。</summary>
    public Vector3 GetTurretLocal() {
        if (turretLocation != null && mapSurface != null)
            return mapSurface.InverseTransformPoint(turretLocation.position);
        return turret != null ? turret.localPosition : Vector3.zero;
    }

    public ArtilleryTask? GetMarkTarget(int index) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: turret unbound");
            return null;
        }

        if (index > artilleries.Count) {
            MelonLogger.Error($"[FCS] GetMarkTarget: index {index} out of range, artillery count: {artilleries.Count}");
            return null;
        }

        var target = artilleries[index].localPosition - turret.localPosition;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        var task = new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = artilleries[index].localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f)
        };
        return task;
    }

    public void SetMarkerWorldPos(int index, Vector3 worldPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (mapSurface == null) return;
        var local = mapSurface.InverseTransformPoint(worldPos);
        local.z = marker.localPosition.z;
        marker.localPosition = local;
    }

    public void ResetMarker(int index)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (turret == null) return;
        marker.localPosition = turret.localPosition;
    }

    /// <summary>指定编号标记的世界坐标(手动任务目标解析与位置提交用)</summary>
    public Vector3 GetMarkerWorldPos(int index)
    {
        if (artilleries.TryGetValue(index, out var marker)) return marker.position;
        return Vector3.zero;
    }

    public List<EntityLocation> GetAllFireMissionEntities() {
        List<EntityLocation> res = new();
        if (fireMissionRoot == null) {
            return res;
        }

        for (var i = 0; i < fireMissionRoot.childCount; ++i) {
            var m = fireMissionRoot.GetChild(i).GetComponent<EntityLocation>();
            if (m != null) res.Add(m);
        }
        return res;
    }
    
}
