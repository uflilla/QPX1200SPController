# Acts on a control of the running QPX1200SPController window by its accessible name.
# Usage: pwsh -NoProfile -File tools/uia.ps1 -Name History [-Action select|invoke|toggle|expand|choose|value|read] [-Index 0] [-Value 12.5]
#        pwsh -NoProfile -File tools/uia.ps1 -Action dump [-Name filter]
# -Index picks the n-th match (0 = first): several elements may share a name.
# expand opens a combo box; choose selects the combo item named -Value; value sets a text box or up-down box to -Value; read prints enabled, toggle state and value;
# dump lists every named element (optionally only names containing -Name).
param([string]$Name, [string]$Action = 'select', [int]$Index = 0, [string]$Value)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
# $env:QPX1200SP_PID picks one instance when several run (e.g. one in the Visual Studio debugger).
$p = Get-Process QPX1200SPController -ErrorAction SilentlyContinue | Where-Object { -not $env:QPX1200SP_PID -or $_.Id -eq [int]$env:QPX1200SP_PID } | Select-Object -First 1
# Find the main window by its title: while a plot's hover popup is open, MainWindowHandle may be the popup.
$root = $A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
  (New-Object System.Windows.Automation.PropertyCondition ($A::ProcessIdProperty), $p.Id)) |
  Where-Object { $_.Current.Name -like 'QPX1200SP*' } | Select-Object -First 1
if (-not $root) { "main window not found"; exit 1 }
$scope = [System.Windows.Automation.TreeScope]::Descendants

if ($Action -eq 'dump') {
  foreach ($e in $root.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition)) {
    $n = $e.Current.Name
    if ($n -and (-not $Name -or $n -like "*$Name*")) { "{0,-14} {1}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $n }
  }
  exit 0
}

$cond = New-Object System.Windows.Automation.PropertyCondition ($A::NameProperty), $Name
$found = $root.FindAll($scope, $cond)
# Count only elements that support the action (a button's caption is a separate Text element with the same name).
$patterns = @{ select = [System.Windows.Automation.SelectionItemPattern]::Pattern; invoke = [System.Windows.Automation.InvokePattern]::Pattern; toggle = [System.Windows.Automation.TogglePattern]::Pattern; expand = [System.Windows.Automation.ExpandCollapsePattern]::Pattern; choose = [System.Windows.Automation.ExpandCollapsePattern]::Pattern }
$all = @($found | Where-Object { -not $patterns.ContainsKey($Action) -or $_.GetSupportedPatterns() -contains $patterns[$Action] })
if ($all.Count -le $Index) { "not found: $Name [$Index] ($($all.Count) match(es))"; exit 1 }
$el = $all[$Index]
switch ($Action) {
  'select' { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
  'invoke' { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  'toggle' { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
  'expand' { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
  'choose' {
    # Opens the combo box, selects the item named -Value, closes it again.
    $ec = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 300
    # An item is named by its text, or (DisplayMemberPath) holds a text element with that name.
    $named = New-Object System.Windows.Automation.PropertyCondition ($A::NameProperty), $Value
    $listItem = New-Object System.Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)
    $item = $el.FindAll($scope, $listItem) | Where-Object { $_.Current.Name -eq $Value -or $_.FindFirst($scope, $named) } | Select-Object -First 1
    if (-not $item) { $ec.Collapse(); "no item '$Value' in $Name [$Index]"; exit 1 }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $ec.Collapse()
  }
  'value' {
    # Up-down boxes expose their text box as a child; set it, then move focus away so the binding updates.
    $target = $el
    $vp = $null
    if (-not $el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
      $edit = $el.FindFirst($scope, (New-Object System.Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit)))
      if (-not $edit) { "no text box in $Name"; exit 1 }
      $target = $edit
      $vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    }
    $target.SetFocus()
    $vp.SetValue($Value)
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
  }
  'read' {
    $state = ''
    $tp = $null; $vp = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) { $state += " toggle=$($tp.Current.ToggleState)" }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $state += " value=$($vp.Current.Value)" }
    "$Name [$Index] enabled=$($el.Current.IsEnabled)$state"
    exit 0
  }
}
"done $Action $Name [$Index]"
