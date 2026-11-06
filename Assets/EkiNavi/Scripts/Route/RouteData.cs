using System;
using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Route
{
    /// <summary>
    /// server/route/export_route.py が書き出す経路 JSON の中身。
    /// 座標は固定原点からのメートルで、x = 東、z = 北。
    /// </summary>
    [Serializable]
    public class RouteData
    {
        public GeoPoint origin;
        public GateInfo start;
        public GateInfo goal;
        public float lengthMeters;
        public RoutePoint[] points;
        public VerticalMove[] verticalMoves;

        public static RouteData Parse(string json)
        {
            var data = JsonUtility.FromJson<RouteData>(json);
            if (data == null || data.points == null || data.points.Length < 2)
                throw new FormatException("経路 JSON に点が2つ以上ありません");
            return data;
        }

        /// <summary>
        /// 出発点を (0, 0, 0) にした座標の列を返す。階が違う点は floorHeight メートルずつ上下にずらす。
        /// </summary>
        public List<Vector3> ToLocalPoints(float floorHeight)
        {
            var first = points[0];
            var result = new List<Vector3>(points.Length);
            foreach (var p in points)
            {
                result.Add(new Vector3(p.x - first.x, (p.ordinal - first.ordinal) * floorHeight, p.z - first.z));
            }
            return result;
        }
    }

    [Serializable]
    public class GeoPoint
    {
        public double lat;
        public double lon;
    }

    [Serializable]
    public class GateInfo
    {
        public string id;
        public string name;
        public float ordinal;
    }

    [Serializable]
    public class RoutePoint
    {
        public float x;
        public float z;
        public float ordinal;
    }

    [Serializable]
    public class VerticalMove
    {
        public string kind; // "stairs" | "escalator" | "elevator"
        public int fromIndex;
        public int toIndex;
    }
}
