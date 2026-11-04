# Mod Removal Auditor (MRA)

**Status: Planning only — no implementation, compiled DLL, or playable release is available.**

A proposed read-only RimWorld 1.7 mod for checking whether content introduced by selected enabled mods remains in a loaded save before disabling those mods.

## Goals

- Search by source mod (package ID), including multi-selection
- Report remaining items, buildings, inventory, equipment, caravan items, and terrain
- Show quantities, holders, and map positions where available
- Clearly distinguish scanned, partially scanned, unsupported, and unknown data
- Keep scanning on-demand and non-destructive, with Japanese and English UI planned

**A zero-finding report would not guarantee that a mod is safe to remove.** Save backups and separate load testing remain necessary.

## Planning documents

- [Design specification](docs/superpowers/specs/2026-11-04-mod-removal-auditor-design.md)
- [Implementation plan](docs/superpowers/plans/2026-11-04-mod-removal-auditor.md)

The specifications and API choices are proposals pending implementation-time verification against the actual RimWorld 1.7 game assemblies.

---

## 日本語

**現在は計画段階です。実装コード・コンパイル済みDLL・配布版はありません。**

RimWorld 1.7向けの「MOD削除前チェック」ツールを計画しています。有効なMODを選び、現在のセーブに残っている追加アイテム、建築物、装備、インベントリ、キャラバンの荷物、床材などを読み取り専用で調査する構想です。

主な機能は、追加元MOD（package ID）による複数選択・検索、検出数と所持者・場所の表示、検査済み／一部検査／未対応／出自不明の区別です。常時監視や自動削除は行わず、日本語・英語のUIを用意する計画です。

**検出結果が0件でも、MODの安全な削除を保証するものではありません。** セーブのバックアップと別途ロード検証が必要です。

- [設計仕様書](docs/superpowers/specs/2026-11-04-mod-removal-auditor-design.md)
- [実装計画書](docs/superpowers/plans/2026-11-04-mod-removal-auditor.md)

RimWorld 1.7のAPIやビルド条件は、実装時に実際のゲーム用DLLで確認する予定です。
