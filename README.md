# Zero-Touch BLE Dynamic Lock System (Approach A: App-less iPhone Edition)

[![Target OS](https://img.shields.io/badge/Windows-10%20%7C%2011-blue.svg)](https://microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

Windows標準の「動的ロック（Dynamic Lock）」の不具合および遅延を解決するため、BLE（Bluetooth Low Energy）のRSSI（受信信号強度）を用いた高速・高精度な自動画面ロック＆プライバシー保護システムです。

**iPhone側に専用アプリを一切インストールすることなく**、WindowsとiPhoneの標準Bluetoothペアリング（Bonding）のみで動作します。

---

## ✨ 主な特徴 (Features)

1. **iOS 特有の BLE MAC アドレスランダム化（IRK）の完全解決**
   - iOSは15分ごとにBLE MACアドレスを変更しますが、Windowsのペアリング済みデバイスデータベースと照合するIdentity Resolving Key (IRK) 解決パイプラインおよびメモリキャッシュ機構により、継続的かつ低遅延な追跡を実現。
2. **1次元カルマンフィルタ (1D Kalman Filter) & 信号減衰タイマー**
   - 人体遮蔽やマルチパス反射による激しいRSSIノイズを平滑化。パケット途絶時にはペナルティ仮想値を注入して自然に減衰。
3. **ヒステリシス状態遷移マシン (State Machine)**
   - `Present`（在席: $\ge -68\text{ dBm}$）、`Warning`（警戒: $-78 \sim -68\text{ dBm}$）、`Away`（離席確定: $\le -78\text{ dBm}$）、`Locked`（ロック中）の4状態とディレイタイマーによりチャタリングを徹底防止。
4. **ヒューマン・アクティビティ・ガード（誤ロック防止安全装置）**
   - `user32.dll` の `GetLastInputInfo` を監視し、**直近3.0秒以内にキーボードやマウス操作がある場合は画面ロックを自動保留**。PC操作中の理不尽なロックを排除。
5. **Stealth Shield（透過クリックスルー警告オーバーレイ）**
   - `WS_EX_LAYERED | WS_EX_TRANSPARENT` により、画面全体の輝度を滑らかに暗転。作業の手を止めずにロック予兆を視覚的にフィードバック。
6. **リアルタイム・オシロスコープ ＆ キャリブレーション・ダッシュボード (WPF)**
   - 外部グラフライブラリ不要の自前描画オシロスコープ（Raw波形 vs カルマン平滑化波形）。
   - 対数距離経路損失モデルによる推定距離表示（例: `approx. 1.4 m`）。
   - ワンクリック・キャリブレーション（現在位置を基準に最適閾値を自動設定）。
   - ウィンドウを閉じている待機時は描画ループを停止し、CPU負荷 0.1% 未満を達成。
7. **タスクトレイ（NotifyIcon）完全常駐**
   - 状態に合わせた動的カラーアイコン描画、リアルタイム距離付きツールチップ、右クリックメニュー。

---

## 📐 アーキテクチャ (Architecture)

```text
ZeroTouchLock/
├── SPECIFICATION.md                 # 詳細要件定義書 (アプローチA + 第7章 GUI Addendum)
├── ZeroTouchLock.csproj             # .NET 8 WPF + WinForms ハイブリッド構成
├── App.xaml / App.xaml.cs           # 多重起動防止Mutex、DIコンテナ、ライフサイクル制御
├── appsettings.json                 # 閾値・カルマン係数・デバイス設定
├── Configuration/
│   └── AppSettings.cs               # 構成クラス ＆ SaveToFile() によるホットリロード
├── Core/
│   ├── BleProximityMonitor.cs       # WinRT BLE Watcher、IRK解決・キャッシュ、GATT Keep-Alive
│   ├── KalmanFilter1D.cs            # 1次元カルマンフィルタ＋信号途絶Decay
│   ├── DistanceEstimator.cs         # 対数距離経路損失モデル (RSSI → 推定距離m)
│   ├── ProximityStateMachine.cs     # ヒステリシス状態遷移機 (Present/Warning/Away/Locked/Paused)
│   └── Contracts.cs                 # 抽象インターフェース群
├── Native/
│   ├── Win32Api.cs                  # User32 (LockWorkStation, GetLastInputInfo), Wtsapi32
│   ├── Win32WorkstationServices.cs  # ロック実行および入力アイドル監視の実装
│   └── SessionMonitor.cs            # SystemEvents.SessionSwitch によるOSセッション監視
├── UI/
│   ├── DashboardWindow.xaml (.cs)   # リアルタイムオシロスコープ・キャリブレーションGUI
│   ├── StealthOverlayForm.cs        # クリックスルー透過暗転オーバーレイ
│   └── TrayIconManager.cs           # タスクトレイ動的アイコン・メニュー
└── tests/
    └── ZeroTouchLock.Tests/         # カルマンフィルタ・状態遷移・距離推定の単体テスト
```

---

## 🚀 クイックスタート (Build & Run)

### 必要要件
- Windows 10 (Build 19041以降) または Windows 11
- .NET 8.0 SDK

### ビルドと発行 (Self-Contained Single File)
```powershell
# リポジトリのクローン
git clone https://github.com/Cacaonib2020/ZeroTouchLock.git
cd ZeroTouchLock

# Windows x64 向け単一実行可能ファイルのビルド
dotnet publish ZeroTouchLock.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

発行完了後、`bin/Release/net8.0-windows10.0.22621.0/win-x64/publish/ZeroTouchLock.exe` を実行します。

### 使い方
1. WindowsのBluetooth設定画面から、お使いのiPhoneをペアリング（Bonding）します。
2. `ZeroTouchLock.exe` を起動します。タスクトレイに緑色のインジケーターアイコンが表示されます。
3. トレイアイコンをダブルクリック（または右クリックから「Open Dashboard」を選択）するとダッシュボードが展開されます。
4. 机の前で **`[Calibrate Here (Present Position)]`** ボタンを押すと、現在の電波強度に合わせて Warning / Away 閾値が自動調整されます。

---

## 🧪 単体テストの実行

```bash
dotnet test tests/ZeroTouchLock.Tests/ZeroTouchLock.Tests.csproj
```

カルマンフィルタの平滑化、信号途絶減衰、ヒステリシス状態遷移ディレイ、ヒューマン・アクティビティ・ガードによる誤ロック防止、対数距離推定モデルの全テストがパスすることを確認できます。

---

## 📄 ライセンス

MIT License
