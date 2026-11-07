using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// カメラの設定（解像度）の一覧から、使うものを選ぶ。
    /// ARCore は既定で 640×480 を選ぶことがあり、そのままだと画面の映像も 640×480 になって、
    /// 遠くの案内板の文字が読めない（KYV46 で確認）。
    /// </summary>
    public static class CameraConfigChooser
    {
        /// <summary>
        /// 長辺が maxLongSide 以下のうち、画素数がいちばん多いものを選ぶ。同じ大きさなら、先にあるもの。
        /// どれも maxLongSide を超えるなら、いちばん小さいもの。
        /// </summary>
        /// <param name="sizes">選べる解像度。</param>
        /// <param name="maxLongSide">長辺の上限（px）。大きすぎると端末が熱くなり、追跡が重くなるため。</param>
        /// <returns>選んだ番号。一覧が空なら -1。</returns>
        public static int Choose(IReadOnlyList<Vector2Int> sizes, int maxLongSide = 1920)
        {
            int best = -1, smallest = -1;
            long bestPixels = -1, smallestPixels = long.MaxValue;
            for (int i = 0; i < sizes.Count; i++)
            {
                var s = sizes[i];
                long pixels = (long)s.x * s.y;
                if (pixels < smallestPixels)
                {
                    smallestPixels = pixels;
                    smallest = i;
                }
                if (Mathf.Max(s.x, s.y) <= maxLongSide && pixels > bestPixels)
                {
                    bestPixels = pixels;
                    best = i;
                }
            }
            return best >= 0 ? best : smallest;
        }
    }
}
