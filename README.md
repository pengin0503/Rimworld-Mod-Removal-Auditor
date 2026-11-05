# Mod Removal Auditor (MRA)

A **read-only, on-demand** RimWorld 1.7 mod: pick one or more enabled mods and see which of their content is still present in the **currently loaded save** — with counts, holders and jump links — before you disable them.

**A result with no detections is NOT a guarantee that a mod is safe to remove.** It only covers the scopes listed in the window's *Coverage* section. Always back up your save and test-load it after disabling a mod.

- packageId: `pengin0503.modremovalauditor` · supported version: 1.7 · languages: English, Japanese
- No Harmony patches, no tick work, no settings file, nothing stored in your save, nothing deleted or converted.

## Usage

1. Load a save. Open the auditor with the small magnifier icon on the bottom bar (next to the menu button), or via *Options → Mod settings → Mod Removal Auditor*.
2. Search and tick one or more mods (Core and DLC are not listed). Press **Scan**. The game stays paused while the window is open; the scan advances in small steps each frame (≤ 300 units or ≈ 2 ms per frame) and can be cancelled.
3. Read the result:
   - **Summary** per mod: `Detected`, `No detected instances` (within the checked scopes) or `Incomplete` (a scope was partial/failed, or content of unknown origin exists). Notes list def types the mod defines that are not scanned, whether it loads its own C# code, and which mods depend on it.
   - **Coverage**: every scope with `checked` / `partially checked` / `FAILED` / `not covered`.
   - **Findings** grouped by mod and category (items, buildings, terrain, contained/equipped, world/caravan, used as material, other) with instance count, stack-based item count, map/holder/cell and source confidence.
4. **Jump** closes the window and selects the thing (or its holder / caravan). Reopening the window shows the last result again (marked as possibly stale). Jump is disabled for things that no longer exist, for changed terrain, and for off-map holders such as world pawns. **Re-scan** replaces the result.

## What is scanned (v1.1)

| Scope | Contents |
|---|---|
| Maps | Every loaded map incl. pocket maps and maps you are not viewing: spawned things, plus everything nested in containers, inventories, apparel, equipment, carried things, corpses, minified things, passing trade ships |
| World | Caravans and cargo, settlement trade stock, travelling transport pods, sites, things and pawns aboard a gravship in flight |
| World pawns | Pawns kept by the world (alive, mothballed, dead) and their gear |
| Terrain | All four saved layers (top, under, foundation, temporary) on every loaded map, aggregated per def |
| Material | A thing made of a mod's stuff (e.g. a vanilla wall made of modded wood) is reported for the stuff's mod |
| Pawn data | For every pawn found above (colonists, animals, prisoners, caravan members, world pawns, pawns in corpses or containers): hediffs, xenogenes and endogenes, xenotype, traits, abilities, pawn kind, childhood/adulthood backstories |
| Bills | Bills (recipes) of every work table and pawn found above, including surgery bills |
| World objects | The definition of every world object and the parts of every site |
| Factions | The definition of every faction |
| Ideoligions | Memes, precepts and culture |
| Quests | The root script of every quest, including ended quests kept in the history |
| Research | Projects with saved progress, applied techprints or anomaly knowledge, and the current project |

**Not covered** (always listed in the window): thoughts/memories, relations, records, royal titles, work and policy assignments; storage, bill-ingredient and outfit/food/drug policy filters; quest internals; tales and art subjects; mod-specific saved data and runtime-generated content; terrain stored by a gravship in flight; unloaded maps and other saves. Research projects whose saved progress is zero are not listed.

**Read-only by design:** some game accessors write while they read (for example `ResearchManager.GetProgress` and `ResearchProjectDef.IsFinished` insert a 0 entry into the saved research dictionary, and `Pawn_GeneTracker.GenesListForReading` / `CustomXenotype` build caches). The auditor does not call them; saved research progress is read from the manager's own dictionary with a single reflection field read and nothing is written. If that field is not present on a future game version, the research scope is reported as partially checked.

## How ownership is decided

Ownership uses only the def's source pack (`Def.modContentPack`) and the loaded packs' def lists — never labels, textures or names:

- `source confirmed`: the owning mod lists this exact def and no other loaded mod declares a def of the same type and name. Implied defs (blueprints, frames, meat, corpses, …) are registered in their mod's def list by the game and count as confirmed.
- `source uncertain`: another mod declares the same type+defName (override/shadowing — removing one may leave the other's version), or the def claims a mod that does not list it (created by code at runtime). Reported for every involved mod.
- `source unknown`: the def has no source pack. Such content is counted separately and makes the verdict `Incomplete`, because it could belong to any mod.

An XML patch that edits another mod's def does not make the patching mod the owner.

## Building

Requires the .NET 10 SDK and a local RimWorld 1.7 installation (the game assemblies are referenced, never copied or committed).

```bash
dotnet test Tests/ModRemovalAuditor.Tests -c Release
```

```bash
RIMWORLD_17_DIR=/path/to/RimWorld dotnet build Source/ModRemovalAuditor/ModRemovalAuditor.csproj -c Release
```

```bash
python3 tools/check_translations.py
```

`RIMWORLD_17_DIR` is the folder containing `Assembly-CSharp.dll` and `UnityStubs.dll`. The build writes `1.7/Assemblies/ModRemovalAuditor.dll`.

## Verification status

- Compiled against RimWorld **1.7.4985 rev1269** assemblies (`Assembly-CSharp`, `UnityStubs`), .NET 10 SDK 10.0.403: 0 warnings, 0 errors.
- 66 engine-independent unit tests (ownership, de-duplication by object and def, aggregation, holder traversal with cycles/depth/broken holders, bounded/cancellable sessions, verdict rules) pass.
- EN/JA translation parity and all code-referenced keys checked statically (165 keys).
- **Not yet verified in game**: actual loading in a 1.7 client, UI layout/clipping, frame time on a large colony, scan results against a fixture save, the bottom-bar icon. Please report problems with a log.

## Repository layout

```text
About/About.xml
1.7/Assemblies/ModRemovalAuditor.dll   # build output of Source/ (no game assemblies)
Defs/MainButtonDefs/                    # bottom-bar button
Languages/{English,Japanese}/
Textures/UI/ModRemovalAuditor/
Source/ModRemovalAuditor/              # Audit/ (core + game adapters), UI/
Tests/ModRemovalAuditor.Tests/         # pure unit tests, no game assemblies needed
tools/check_translations.py
docs/superpowers/                      # design specification and implementation plan
```

---

## 日本語

RimWorld 1.7用の**読み取り専用・必要時のみ実行**のMODです。有効なMODを1つ以上選ぶと、そのMOD由来の内容が**現在読み込んでいるセーブ**にどれだけ残っているかを、個数・所持者・ジャンプ先付きで表示します。MODを無効化する前の確認に使えます。

**検出0件は、MODを安全に削除できる保証ではありません。** ウィンドウの「調査範囲」に記載した範囲だけの結果です。必ずセーブをバックアップし、MODを無効化した後に試しにロードして確認してください。

- packageId：`pengin0503.modremovalauditor`／対応バージョン：1.7／言語：日本語・英語
- Harmonyパッチ・毎tickの処理・設定ファイル・セーブへの追加データはありません。削除や変換も一切行いません。

### 使い方

1. セーブを読み込み、画面下部の虫眼鏡アイコン（メニューボタンの隣）、または「オプション→MODの設定→Mod Removal Auditor」から開きます。
2. MODを検索してチェックを入れ（Core・DLCは表示されません）、「調査」を押します。ウィンドウを開いている間はゲームが一時停止し、毎フレーム少しずつ（最大300単位または約2ミリ秒）調査します。途中で中止できます。
3. 結果の見方：
   - **概要**：MODごとに「検出あり」「調査した範囲では検出なし」「不完全」（一部の範囲が部分的・失敗、または出自不明の内容がある）を表示します。調査しない種類のDef、独自C#コードの有無、依存しているMODも注記します。
   - **調査範囲**：範囲ごとに「調査済み」「一部のみ調査」「失敗」「対象外」を表示します。
   - **検出結果**：MOD→分類（アイテム、建築物、床・地形、収納・装備中、ワールド・キャラバン、素材として使用、その他）ごとに、個数・スタック数・マップ／所持者／座標・出自の確度を表示します。
4. 「移動」でウィンドウを閉じ、その物（または所持者・キャラバン）を選択します。ウィンドウを開き直すと前回の結果を（古い可能性がある旨と共に）再表示します。既に存在しない物、床が変わった場所、ワールドポーンなどマップ外のものは移動できません。「再調査」で結果を更新します。

### 調査範囲（v1.1）

- **マップ**：表示していないマップ・ポケットマップを含む全マップの配置物と、容器・インベントリ・衣服・装備・運搬物・死体・梱包物・通過中の交易船の中身
- **ワールド**：キャラバンと積み荷、拠点の交易在庫、移動中の輸送ポッド、サイト、飛行中のグラビシップの物とポーン
- **ワールドポーン**：ワールドに保存されているポーン（生存・休眠・死亡）とその所持品
- **床・地形**：全マップの4つの保存レイヤー（上層・下層・基礎・一時）
- **素材**：MODの素材で作られた物（例：MODの木材で作ったバニラの壁）は素材側のMODとして報告
- **ポーンのデータ**：上記で見つかったすべてのポーン（入植者、動物、囚人、キャラバンの一員、ワールドポーン、死体や容器の中のポーン）の健康状態、遺伝子（異種・内在）、ゼノタイプ、特性、能力、ポーンの種類、経歴
- **作業指示**：上記で見つかった作業台とポーンの作業指示（手術の予定を含む）
- **ワールド上の物**：すべてのワールド上の物の定義と、各サイトの構成要素
- **勢力**：すべての勢力の定義
- **イデオロギー**：ミーム、戒律、文化
- **クエスト**：各クエストの元になるスクリプト（履歴に残る終了済みクエストを含む）
- **研究**：進捗・使用済み技術設計図・アノマリーの知識が保存されている研究と、現在の研究

**対象外**（常にウィンドウに表示）：思考・記憶、人間関係、記録、称号、仕事や方針の割り当て、保管場所・作業指示の材料・服装／食事／薬物の方針のフィルター、クエスト内部、物語や美術品の題材、MOD独自の保存データと実行時生成の内容、飛行中グラビシップの床、読み込まれていないマップや他のセーブ。保存された進捗が0の研究は表示しません。

**読み取り専用の徹底**：ゲームの一部の取得処理は読み取り時に書き込みを行います（例：`ResearchManager.GetProgress` や `ResearchProjectDef.IsFinished` は保存される研究の辞書に0の項目を追加し、`GenesListForReading`・`CustomXenotype` はキャッシュを作ります）。本MODはこれらを呼び出しません。保存された研究の進捗は、管理クラスの辞書を一度だけリフレクションで読み取り、何も書き込みません。将来のバージョンでこの項目がなくなった場合、研究の範囲は「一部のみ調査」と表示されます。

### 出自の判定

名前・ラベル・テクスチャではなく、Defの追加元パック（`Def.modContentPack`）と各MODのDef一覧だけで判定します。

- 「出自確認済み」：所有MODがそのDefを一覧に持ち、他のMODが同じ種類・同じ名前のDefを定義していない。設計図・建設中・肉・死体などの自動生成Defもゲームが所有MODの一覧に登録するため確認済みになります。
- 「出自不確定」：別のMODが同じ名前のDefを定義している（上書き）、または所有MODの一覧にない（実行時にコードで作られた）。関係するすべてのMODに表示します。
- 「出自不明」：追加元パックの記録がない。どのMODのものでもあり得るため別枠で数え、判定を「不完全」にします。

他MODのDefをXMLパッチで変更しても、パッチ側のMODが所有者になることはありません。

### 検証状況

- RimWorld **1.7.4985 rev1269** のアセンブリ（`Assembly-CSharp`、`UnityStubs`）に対してコンパイル済み（警告0・エラー0）。
- ゲームに依存しない単体テスト66件（出自判定、物とDefの組による重複排除、集計、循環・深さ・破損容器を含む走査、上限付き・中止可能な調査、判定規則）が成功。
- 日英の翻訳キー一致と、コードから参照する全キーの存在を静的に確認（165キー）。
- **ゲーム内では未検証**：1.7クライアントでの読み込み、UIの表示崩れ、大規模コロニーでのフレーム時間、テスト用セーブでの検出結果、下部バーのアイコン。問題があればログと共に報告してください。

### 設計資料

- [設計仕様書](docs/superpowers/specs/2026-11-04-mod-removal-auditor-design.md)
- [実装計画書](docs/superpowers/plans/2026-11-04-mod-removal-auditor.md)
