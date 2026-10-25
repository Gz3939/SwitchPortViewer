# SwitchPortViewer
# 交換器埠配置檢視器
<img width="1896" height="781" alt="image" src="https://github.com/user-attachments/assets/2bed7910-c257-42f0-bf0e-f09151457733" />

<img width="1803" height="454" alt="image" src="https://github.com/user-attachments/assets/04b1269c-148c-434b-a1a1-21ad704c5bae" />


Windows .NET 8 桌面程式，讀取 Aruba/HP ProCurve、HPE Comware、HPE OfficeConnect、Zyxel XGS 配置檔（`.pcc`、`.cfg`、`.conf`、`.txt`、`.log` 或無副檔名）。

## 使用

1. 執行 `SwitchPortViewer-Windows` 資料夾中的 `SwitchPortViewer.exe`。
2. 按「開啟配置檔」選擇檔案。可反覆開啟其他配置檔，面板會立即更新。
3. 上方以雙排實體面板排列埠號，依檔案內容自動增加埠與面板區段。
4. VLAN 總表列出每個 VLAN 的 untagged、tagged 埠及數量；「有 Tagged VLAN 的埠」區集中顯示所有帶標記的埠。
5. 最下方列出每埠的類型、PVID、untagged/802.1Q tagged VLAN、聚合群組及管理設定。

也可以把配置檔完整路徑作為啟動參數：`SwitchPortViewer.exe "C:\path\switch.pcc"`。

## 判讀規則

- PVID 優先採用配置檔明示值；未明示時依該格式預設值或 untagged VLAN 推定。
- `tagged` VLAN 列於「802.1Q Tagged」。同時有 untagged 與 tagged 時標為「802.1Q 混合埠」。
- `trunk ... lacp` 的實體成員繼承 `Trk1` 等邏輯群組的 VLAN 設定。
- Comware 的 `port trunk permit vlan all` 按檔案中宣告的 VLAN 展開；OfficeConnect 的 `vlan tagging` 及 Zyxel 的 `fixed`/`untagged` 會分別判讀。
- Zyxel 允許同一實體埠在多個 VLAN 以 untagged 列出；因此畫面將 PVID 與 untagged VLAN 清單分別顯示。
- 未被明確改動的埠按 Aruba/HP 預設值視為 VLAN 1 untagged。
- 埠號上限從檔內引用的埠推定；如果配置檔沒有提及某些實體埠，程式無法知道其存在。
- 配置檔只提供設定，無法反映即時連線 Up/Down、流量、PoE 或 SFP 模組狀態。

原始碼位於本資料夾；以 `dotnet build SwitchPortViewer.csproj -c Release` 可重新編譯。
