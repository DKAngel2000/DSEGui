# AGENTS.md — DSEGui standalone handoff

## Current behavior

### dse.ini Steam hook dependency (2026-11-16)
- When saving `dse.ini`, the editor warns if `coldloaderhooks=true` or `steamApiForward=true` while `steamHooks=false`.
- The prompt offers to enable Steam hooks. Yes checks the Steam hooks option and saves `steamHooks=true`; No keeps the selected value and saves as-is.
- Implementation is in `MainWindow.xaml.cs`, in `BtnSaveIni_Click`. The matching Vector module implementation is in `F:\Projects\Vector\Vector\Modules\Dse\DseView.xaml.cs`.
