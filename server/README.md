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

http://localhost:8090/survey は、現地で吊り下げ看板の位置と向きを地図に記録し、`signs.json` に貼れる形で書き出すページです（今は2階だけ）。① 看板の真下、② 看板の表を見上げている自分の位置の順にタップすると、向きが決まります。裏面にも絵柄がある看板は、反対向きの別の看板として2件記録します。記録はそのブラウザの中にだけ残ります。

スマホで開くときは、同じネットワークにつないで、`--host 0.0.0.0` を付けて起動し、`http://<PC の IP アドレス>:8090/survey` を開きます。http で開くとコピーのボタンが使えないことがあるので、そのときは文字を長押ししてコピーします。

経路 API に `"sameFloor": true` を付けると、出発の階から出ない経路だけを返します（同じ階の中の段差の階段は通ることがあります）。同じ階で行けなければ `no_route` です。

Gemini のキーがなくても動きます（`GEMINI_BACKEND=stub`、既定）。このときは辞書だけで行き先を判定します。

`POST /v1/sign/identify` は、アプリが撮ったカメラの画像（JPEG を base64 にしたもの）と候補の案内板の ID を受け取り、Gemini でどの板が写っているかと、画像の中の枠を返します。`stub` のときは `status` が `unavailable` になります。見分けやすくするため、`signs.json` の各板に `text`（板に書いてある主な文字）を足せます。

| 環境変数 | 意味 |
|---|---|
| `GEMINI_BACKEND` | `stub`（辞書のみ）、`apikey`（Gemini API のキー）、`vertex`（Cloud Run のサービスアカウント） |
| `GEMINI_API_KEY` | `apikey` のときのキー。コミットしない |
| `GOOGLE_CLOUD_PROJECT`、`GOOGLE_CLOUD_LOCATION` | `vertex` のときの接続先 |
| `GEMINI_MODEL` | 既定は `gemini-3.5-flash-lite` |
| `GEMINI_VISION_MODEL` | 案内板の画像を見分けるモデル。空なら `GEMINI_MODEL` と同じ |
| `GEMINI_DAILY_LIMIT` | 1日の Gemini の呼び出し上限（インスタンスごと）。超えると辞書に切り替える |
| `API_RATE_PER_MINUTE` | 1分あたりの経路 API の上限（インスタンスごと、全員の合計） |
| `GEMINI_RATE_PER_MINUTE` | 1分あたりの行き先 API と、案内板の判別 API それぞれの上限（インスタンスごと、全員の合計） |
| `DEMO_VIDEO_URL`、`DEMO_APK_URL` | ページに出すリンク |
| `INTEGRITY_MODE` | `/v1/sign/identify` で Play Integrity のトークン（`X-Integrity-Token`）を確かめるか。`off`（既定）、`log`（記録だけ）、`enforce`（通らなければ 401） |
| `INTEGRITY_PACKAGE` | 期待するアプリのパッケージ名。既定は `com.ekinavi.app` |
| `INTEGRITY_CERT_SHA256` | 許すアプリの署名証明書の SHA-256（keytool の `AB:CD:...` の形でよい）。カンマ区切りで複数。空なら確かめない |
| `INTEGRITY_MAX_AGE_SECONDS` | トークンの古さの上限（秒）。既定 600 |

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

`Dockerfile` で Cloud Run に配置します。経路パックをイメージに焼き込むので、地図データ本体は入りません。権限の付け方、確かめ方、アプリへの URL の入れ方までの手順は [docs/cloud-run-deploy.md](../docs/cloud-run-deploy.md) にあります。次のコマンドは、その手順の中心の部分です。

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
