# サーバーとデータ

経路 API、行き先 API、審査員が開くページを持つサーバーと、地図データから経路パックを作る処理です。役割の間の取り決めは [contracts/README.md](../contracts/README.md) にあります。

## 動かす

Python 3.13 が必要です（`networkx==3.7` は 3.11 では入りません。Cloud Run のイメージも 3.13）。

```
cd server
python3.13 -m venv .venv
.venv/bin/pip install -r requirements-dev.txt
.venv/bin/uvicorn app.main:create_app --factory --port 8090
```

Windows（PowerShell）では次のとおりです。以下の `.venv/bin/` は `.venv\Scripts\` に読み替えてください。

```
cd server
py -3.13 -m venv .venv
.venv\Scripts\pip install -r requirements-dev.txt
.venv\Scripts\uvicorn app.main:create_app --factory --port 8090
```

http://localhost:8090 を開くと、審査員が開くページで経路と行き先の判定を試せます。API の説明は http://localhost:8090/docs にあります。

Gemini のキーがなくても動きます（`GEMINI_BACKEND=stub`、既定）。このときは辞書だけで行き先を判定します。

| 環境変数 | 意味 |
|---|---|
| `GEMINI_BACKEND` | `stub`（辞書のみ）、`apikey`（Gemini API のキー）、`vertex`（Cloud Run のサービスアカウント） |
| `GEMINI_API_KEY` | `apikey` のときのキー。コミットしない |
| `GOOGLE_CLOUD_PROJECT`、`GOOGLE_CLOUD_LOCATION` | `vertex` のときの接続先 |
| `GEMINI_MODEL` | 既定は `gemini-3.5-flash-lite` |
| `GEMINI_DAILY_LIMIT` | 1日の Gemini の呼び出し上限（インスタンスごと）。超えると辞書に切り替える |
| `API_RATE_PER_MINUTE` | 1分あたりの経路 API の上限（インスタンスごと、全員の合計） |
| `GEMINI_RATE_PER_MINUTE` | 1分あたりの行き先 API の上限（インスタンスごと、全員の合計） |
| `DEMO_VIDEO_URL`、`DEMO_APK_URL` | ページに出すリンク |

## 経路パックを作り直す

案内板（`station_data/signs.json`）、路線と改札の対応（`lines.json`）、手で直した線（`curated_lines.json`）、現地の差分（`overrides.csv`）を変えたら、経路パックを作り直してコミットします。

地図データは容量が大きいのでリポジトリに入れていません。G空間情報センターから統合版と分割版の zip を取得して、`server/data/` に展開してください。統合版はネットワークと改札の位置に、分割版は事業者の判定に使います。zip の中のファイル名は Shift_JIS なので、Mac の標準の展開では文字化けすることがあります。

```
.venv/bin/python -m pack.build \
    --integrated "data/新宿駅周辺屋内地図オープンデータ_統合版（Shapefile）" \
    --split "data/新宿駅周辺屋内地図オープンデータ（Shapefile）"
.venv/bin/python -m app.export_contracts
```

現地の差分は `overrides.csv` に1行ずつ書きます。

| type | target | 意味 |
|---|---|---|
| `close_link` | リンクの key | その通路を経路から外す |
| `close_gate` | 改札の ID | その改札を行き先から外す |

## 確かめる

```
.venv/bin/python -m pytest
```

地図データの場所を `EKINAVI_INTEGRATED` と `EKINAVI_SPLIT` に入れると、経路パックを作り直して、コミットしてあるものと一致するかも確かめます。

行き先の判定の評価は次のとおりです。`eval/cases.json` の作った文だけを送ります。

```
GEMINI_BACKEND=apikey .venv/bin/python -m eval.run
```

## 配置

`Dockerfile` で Cloud Run に配置します。経路パックをイメージに焼き込むので、地図データ本体は入りません。次のコマンドは未実行の例です。課金の担当とプロジェクトが決まってから確かめてください。

```
gcloud run deploy eki-navi --source . --region asia-northeast1 \
    --allow-unauthenticated --max-instances 3 \
    --set-env-vars GEMINI_BACKEND=vertex,GOOGLE_CLOUD_PROJECT=<プロジェクト>
```

回数の上限はインスタンスごとの全員の合計で、費用の上限として置いています。利用者ごとの制限ではないので、大量に呼ばれると他の利用者も断られます。`--max-instances` を必ず付け、予算アラートも設定してください（通知だけで止まりません）。

## 古い書き出し（route/）

`route/` は PR #2 で作った分割版からの書き出しです。`network.py` の座標の定義は `pack.build` でも使っています。`export_route.py` は経路パックと内容が違うので、Unity 側の読み込みを API に移したら整理します。

## 出典

地図データは「新宿駅周辺屋内地図データ」（国土交通省）（https://www.geospatial.jp/ckan/dataset/mlit-indoor-shinjuku-r2）を加工して使っています。
