# 経路探索（試作）

新宿駅周辺屋内地図オープンデータ（Shapefile）の歩行空間ネットワークから、改札から改札までの最短経路を計算し、Unity が読める JSON に書き出します。

## 準備

データは容量が大きいのでリポジトリには入れていません。「新宿駅周辺屋内地図オープンデータ（Shapefile）」の zip を各自ダウンロードして、`server/data/` に展開してください（このフォルダは Git の対象外です）。zip の中のファイル名は Shift_JIS なので、Mac の標準の展開では文字化けすることがあります。

```
cd server
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
```

## 使い方

改札IDの一覧:

```
.venv/bin/python -m route.export_route "data/新宿駅周辺屋内地図オープンデータ（Shapefile）" --list
```

経路を書き出す（例：JR 南改札 → 新南口改札）:

```
.venv/bin/python -m route.export_route "data/新宿駅周辺屋内地図オープンデータ（Shapefile）" \
    "JR新宿駅:南改札" "JR新宿駅新南改札:新南口改札" \
    ../Assets/EkiNavi/Routes/jr_south_to_new_south.json
```

## 座標の決まり

- 単位はメートル
- 原点は北緯 35.6900°・東経 139.7000° に固定（仮。`route/network.py` の `ORIGIN_LAT` / `ORIGIN_LON`）
- 軸は Unity に合わせて x = 東、z = 北、y = 上
- 階は `ordinal`（0 = 地上階、-1 = 地下1階、2 = 2階）

改札は各駅の `*_Opening.shp` のうち名前に「改札」を含むものを使い、同じ階のいちばん近いネットワークのノードにつないでいます。

## 出典

地図データは「新宿駅周辺屋内地図オープンデータ」（国土交通省）を加工して使っています。利用条件はデータの配布ページで確認してください。
