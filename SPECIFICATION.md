# Project Specification: Zero-Touch BLE Dynamic Lock System (Approach A: App-less iPhone Edition)

## 1. プロジェクト概要 (Overview)
Windows標準の「動的ロック（Dynamic Lock）」の不具合および応答遅延を解決するため、BLE (Bluetooth Low Energy) のRSSI（受信信号強度）を用いた高速・高精度な自動画面ロック＆プライバシー保護システムを構築する。
本システム（アプローチA）は**iPhone側に専用アプリをインストールすることなく**、WindowsとiPhoneの標準Bluetoothペアリング（Bonding）のみで動作する常駐型バックグラウンドアプリケーションである。

## 2. 推奨技術スタック (Tech Stack)
- **Target OS:** Windows 10 (Build 19041以降) / Windows 11
- **Language / Runtime:** C# (.NET 8.0, `net8.0-windows10.0.22621.0`) または Rust (`windows` crate)
  - ※本仕様書はC# (.NET 8) を基準にAPIを記述する。システムトレイ常駐のWorker ServiceまたはWinForms/WPF（ウィンドウ非表示・NotifyIconのみ）として構成すること。
- **Core APIs:**
  - **WinRT Bluetooth API:** `Windows.Devices.Bluetooth`, `Windows.Devices.Bluetooth.Advertisement`, `Windows.Devices.Enumeration`
  - **Win32 P/Invoke (`User32.dll`, `Wtsapi32.dll`):** `LockWorkStation`, `GetLastInputInfo`, `WTSRegisterSessionNotification`

---

## 3. コア技術要件・アーキテクチャ (Core Technical Requirements)

### 3.1. iPhoneのBLE特定とMACアドレスランダム化（IRK）対策【最重要】
iOSはプライバシー保護のため、BLEアドバタイズパケットのMACアドレス（Random Private Resolvable Address）を約15分周期で変更する。単純なMACアドレス固定スキャンは使用してはならない。以下のいずれか（または組み合わせ）でペアリング済みiPhoneを特定・追跡すること。

1. **ペアリング済みデバイスの列挙と監視:**
   - `DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))` を使用し、Windowsとペアリング済みのBLEデバイス一覧から対象のiPhone（Device NameまたはDevice ID）を特定する。
2. **BLE Advertisement WatcherによるリアルタイムRSSI取得:**
   - `BluetoothLEAdvertisementWatcher` (`ScanningMode = BluetoothLEScanningMode.Active`) を起動する。
   - `Received` イベント発火時、`BluetoothLEAdvertisementReceivedEventArgs.BluetoothAddress` を受け取り、`BluetoothLEDevice.FromBluetoothAddressAsync(address)` を呼び出す。
   - 取得した `BluetoothLEDevice.DeviceInformation.IsPaired` が `true` であり、かつ事前登録したターゲットiPhoneの `DeviceId`（または `BluetoothDeviceId`）と一致する場合のみ、そのパケットの `RawSignalStrengthInDBm` (RSSI) を抽出する。
   - ※パフォーマンス最適化のため、一度解決された一時BluetoothAddressはメモリ上でキャッシュし、アドレス変更（タイムアウト）時のみ再解決を行うこと。
3. **GATT Keep-Alive / ANCS購読による接続維持（フォールバック機構）:**
   - iPhoneがスリープ状態に入りアドバタイズ頻度が低下する場合に備え、ターゲットの `BluetoothLEDevice` に対してGATTサービス（例：Battery Service `0x180F` または ANCS `7905F431-B5CE-4E99-A40F-4B1E122D00D0`）への接続セッションを維持し、切断イベント（`ConnectionStatusChanged`）も離席判定のトリガーとして併用すること。

### 3.2. RSSI信号処理：1次元カルマンフィルタ (1D Kalman Filter)
生のRSSI値は人体遮蔽やマルチパス反射により激しく変動する。誤ロックを防ぐため、以下のパラメータを持つ1次元カルマンフィルタを実装し、平滑化された推定RSSI値（$\hat{x}$）を算出すること。

- **状態方程式・更新ロジック:**
  - プロセスノイズ共分散 $Q = 0.05$ （設定ファイルで変更可能とすること）
  - 観測ノイズ共分散 $R = 2.0$ （設定ファイルで変更可能とすること）
  - パケット欠損（1.5秒以上アドバタイズを受信できない場合）が継続した際は、ペナルティとして仮想観測値 `-100 dBm` をフィルタに入力し、推定RSSIを緩やかに減衰させる減衰タイマー（Decay Timer）を実装すること。

### 3.3. ヒステリシス状態遷移マシン (State Machine & Hysteresis)
チャタリング（連続ロックや画面のチラつき）を防止するため、以下の4つの状態を定義し、閾値にヒステリシス（履歴現象）を持たせること。

| 状態 (State) | 条件 (Condition) | アクション (Action) |
| :--- | :--- | :--- |
| **1. Present (在席)** | 推定RSSI $\ge -68\text{ dBm}$ | 通常状態。Warning状態から復帰した場合はプライバシーシールドを解除する。 |
| **2. Warning (警戒/準離席)** | $-78\text{ dBm} < \text{推定RSSI} < -68\text{ dBm}$ が **2.0秒以上** 継続 | **Stealth Shield（段階的保護）発動：** 画面全体を薄暗くする、またはオーバーレイウィンドウを表示し、離席しかけていることを視覚的に通知する。 |
| **3. Away (離席確定)** | 推定RSSI $\le -78\text{ dBm}$ が **3.5秒以上** 継続、またはGATT完全切断が **4.0秒以上** 継続 | Win32 API `LockWorkStation()` を実行し、状態を `Locked` へ遷移する。 |
| **4. Locked (ロック中)** | OSのセッションがロック状態 | 無駄な `LockWorkStation()` の連打を停止。`SessionUnlock` イベントを検知するまで待機（またはスキャン頻度を落として省電力化）。 |

### 3.4. ヒューマン・アクティビティ・ガード（誤ロック防止安全装置）
電波環境の悪化により、ユーザーがPCを操作中であるにもかかわらずRSSIが低下するケースを防ぐため、**入力ガード機構**を実装すること。
- Win32 API `GetLastInputInfo()` を用いて、マウス移動またはキーボード入力からの経過時間（Idle Time）を取得する。
- **ルール:** 「最後の入力から **3.0秒以内** である場合」は、仮にRSSIが `Away` 閾値を下回っても `LockWorkStation()` の実行を保留（キャンセル）すること。

### 3.5. セッション状態の監視 (WTS Session Notification)
- `WTSRegisterSessionNotification` または `Microsoft.Win32.SystemEvents.SessionSwitch` を購読する。
- `SessionSwitchReason.SessionLock` および `SessionSwitchReason.SessionUnlock` を正確にトラッキングし、すでにロックされている状態で二重にロック処理が走らないように制御すること。

---

## 4. モジュール構成案 (Project Structure)
Antigravityは以下のモジュール分割に従ってクリーンなコードを生成すること。

```text
ZeroTouchLock/
├── Program.cs                  # エントリポイント、多重起動防止(Mutex)、DI構成
├── appsettings.json            # 閾値、カルマンフィルタ係数、ターゲットデバイス名の設定
├── Core/
│   ├── BleProximityMonitor.cs  # WinRT BLE Watcher、ペアリング済みiPhone解決、RSSI抽出
│   ├── KalmanFilter1D.cs       # 1次元カルマンフィルタおよび信号減衰(Decay)計算
│   └── ProximityStateMachine.cs# Present / Warning / Away / Locked の状態遷移管理
├── Native/
│   ├── Win32Api.cs             # User32.dll (LockWorkStation, GetLastInputInfo) P/Invoke定義
│   └── SessionMonitor.cs       # SystemEvents.SessionSwitch ラッパー
└── UI/
    ├── TrayIconManager.cs      # タスクトレイ常駐アイコン、リアルタイムRSSI表示、一時停止トグル
    └── StealthOverlayForm.cs   # Warning状態時に画面にブラー/暗転をかける透過クリックスルーウィンドウ
```

---

## 5. 設定ファイル仕様 (`appsettings.json`)

ハードコードを排除し、実行環境（机の配置やPCのBluetoothアンテナ感度）に合わせてチューニング可能とすること。

```json
{
  "TargetDevice": {
    "DeviceNameKeyword": "iPhone",
    "PairedDeviceId": ""
  },
  "KalmanFilter": {
    "ProcessNoiseQ": 0.05,
    "MeasurementNoiseR": 2.0,
    "SignalLossTimeoutMs": 1500,
    "VirtualDecayRssi": -95.0
  },
  "Thresholds": {
    "PresentRssiDbm": -68.0,
    "WarningRssiDbm": -72.0,
    "AwayRssiDbm": -78.0,
    "WarningDurationMs": 2000,
    "AwayDurationMs": 3500,
    "UserInputGuardMs": 3000
  },
  "Features": {
    "EnableStealthOverlay": true,
    "OverlayOpacity": 0.75
  }
}
```

---

## 6. 実装ステップ（Antigravityへの実行指示）

1. **Step 1 (Setup & Native Wrapper):** `.csproj` の作成（`TargetFramework` を `net8.0-windows10.0.22621.0` に設定し、WinRT APIを有効化）と、`Win32Api.cs`（`LockWorkStation`, `GetLastInputInfo`）の実装。
2. **Step 2 (Signal Processing):** `KalmanFilter1D.cs` と `ProximityStateMachine.cs` を単体テスト可能な純粋クラスとして実装。
3. **Step 3 (BLE Engine):** `BleProximityMonitor.cs` を実装。起動時にペアリング済みBLEデバイス一覧をコンソール（またはログ）に出力し、iPhoneを自動検出してRSSI監視を開始するロジックを構築。
4. **Step 4 (UI & Integration):** システムトレイアイコン（現在の推定RSSIとステータスをツールチップにリアルタイム表示、手動での一時無効化メニュー付き）および `StealthOverlayForm` を統合し、ビルドエラーがないことを確認せよ。


---

# UI & Dashboard Specification Addendum (Section 7)

## 7. グラフィカルUIおよびタスクトレイ常駐仕様 (GUI & System Tray Architecture)

本システムは普段はシステムトレイ（タスクトレイ）に静かに常駐し、トレイアイコンのクリック時にのみ、モダンなダークテーマの「テレメトリ＆コンフィグ・ダッシュボード」を展開する構成とする。

### 7.1. UI技術スタックとライフサイクル制御
- **UI Framework:** WPF (`UseWPF=true`) + WinForms (`UseWindowsForms=true` ※`NotifyIcon`用) のハイブリッド構成（.NET 8.0）。
- **Theme / Styling:** モダンなダークUI（背景色 `#121212` 〜 `#1E1E1E`、アクセントカラー：在席時 `#00E676`、警戒時 `#FFEA00`、離席時 `#FF1744`）。
- **ウィンドウのライフサイクル（重要）:**
  - `App.xaml` の `ShutdownMode` は `OnExplicitShutdown` に設定する。
  - ダッシュボードウィンドウ（`DashboardWindow.xaml`）の `Closing` イベントをフックし、`e.Cancel = true; this.Hide();` とすることで、×ボタン押下時はプロセスを終了させずタスクトレイへ格納する。
  - **省電力制御:** `DashboardWindow.IsVisible == false` の間は、UIスレッドへのグラフ描画イベント通知（`Dispatcher.Invoke`）を完全に停止し、バックグラウンド監視時のCPU負荷を0.1%未満に抑えること。

### 7.2. ダッシュボード画面の構成要素 (`DashboardWindow.xaml`)
ウィンドウはシングルページ完結のダッシュボードとし、以下の4つのセクションを配置する。

#### ① ステータスメーター & 推定距離パネル (Top Header)
- **Current State Badge:** 現在の状態（`PRESENT`, `WARNING`, `AWAY`, `LOCKED`, `PAUSED`）を大型のカラーバッジで表示。
- **Signal Metrics:**
  - `Filtered RSSI`: カルマンフィルタ適用後の現在値（例: `-64.2 dBm`）
  - `Raw RSSI`: 生の最新受信値（例: `-68 dBm`）
  - `Estimated Distance`: 以下の対数距離経路損失モデルで算出した推定距離（例: `approx. 1.4 m`）を表示。
    $$d = 10^{\frac{\text{TxPower} - \text{FilteredRSSI}}{10 \cdot n}}$$
    （初期値: $\text{TxPower} = -59.0$, 環境係数 $n = 2.2$）
- **Last Input Idle Timer:** 最後のマウス/キーボード操作からの経過秒数（例: `Idle: 0.4s [Guard Active]`）を表示。

#### ② リアルタイム RSSI オシロスコープ・チャート (Center Main Panel)
外部グラフライブラリの依存エラーを避けるため、WPFの `Canvas` および `Polyline` を用いて、直近60秒間（または直近120サンプル、200ms周期）のリアルタイム折れ線グラフを自前描画すること（Y軸範囲: `-100 dBm` 〜 `-30 dBm`）。
- **Series 1 (Raw RSSI):** 薄いグレーまたは半透明シアン（`#4000E5FF`）の細線で描画。
- **Series 2 (Kalman Filtered RSSI):** ネオングリーン（`#00E676`）の太線（`StrokeThickness="2.5"`）で滑らかに描画。
- **Threshold Lines (水平ガイドライン):**
  - `WarningRssiDbm` の位置に黄色の破線を描画。
  - `AwayRssiDbm` の位置に赤色の破線を描画。
  - ユーザーが下部スライダーを動かすと、この水平ラインもリアルタイムに上下移動すること。

#### ③ インタラクティブ・コンフィグ＆キャリブレーションパネル (Bottom Left)
設定値をスライダー（`Slider`）および数値入力でリアルタイムに変更でき、`Save & Apply` 押下時にメモリ上の監視エンジンへの即時反映（ホットリロード）と `appsettings.json` への永続化を行う。
- **Target Device Selector:** ペアリング済みBLEデバイス一覧をドロップダウン（`ComboBox`）で表示し、監視対象のiPhoneをGUIから選択可能にする。
- **Sliders:**
  - `Warning Threshold (dBm)`: 範囲 `-95` 〜 `-45`
  - `Away (Lock) Threshold (dBm)`: 範囲 `-100` 〜 `-50`
  - `Away Duration (sec)`: 範囲 `1.0` 〜 `15.0` 秒
  - `Kalman Smoothing (R value)`: 範囲 `0.1`（高レスポンス）〜 `10.0`（強平滑化）
  - `Enable Stealth Overlay`: チェックボックス（Warning時の画面暗転ON/OFF）

#### ④ クイック・テスト＆一時停止コントロール (Bottom Right)
- **`[Calibrate Here (Present)]` ボタン:** 現在のFiltered RSSIを取得し、そこから `-6 dBm` を `Warning`、`-12 dBm` を `Away` 閾値として自動セットするワンクリック調整機能。
- **`[Test Stealth Overlay (3s)]` ボタン:** 画面暗転オーバーレイの見た目を確認するため、3秒間だけテスト発動するボタン。
- **`[Pause Monitoring]` トグルスイッチ:** 一時的に自動ロックを無効化するマスタースイッチ（タスクトレイのコンテキストメニューとも連動）。

### 7.3. タスクトレイ（NotifyIcon）の仕様 (`TrayIconManager.cs`)
- **動的ツールチップ:** マウスホバー時に `ZeroTouchLock - [PRESENT] -63.5 dBm (1.2m)` のように現在のステータスをリアルタイム表示。
- **状態別アイコン色:** 状態（Present=緑、Warning=黄、Away/Paused=赤/灰）に応じてトレイアイコンのインジケーター色を動的に生成（`Bitmap` / `Graphics` で小さな円を描画して `Icon.FromHandle` で変換）。
- **コンテキストメニュー（右クリック）:**
  1. `Open Dashboard` (太字・ダブルクリック時のデフォルト動作)
  2. `Pause Monitoring (Toggle)`
  3. `Lock Now` (即座に `LockWorkStation()` を実行)
  4. `Exit` (リソースを破棄して完全終了)
