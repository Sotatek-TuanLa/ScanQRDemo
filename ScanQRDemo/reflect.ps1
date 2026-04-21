$Path = "C:\Users\admin\Desktop\ScanQRDemoApp\ScanQRDemo\ScanQRDemo\MVSDK_Net.dll"
$Asm = [Reflection.Assembly]::LoadFrom($Path)
$CameraType = $Asm.GetType("MVSDK_Net.MyCamera")

Write-Host "--- IMV_EnumDevices Parameters ---"
$CameraType.GetMethod("IMV_EnumDevices").GetParameters() | ForEach-Object {
    Write-Host "  Name: $($_.Name)  Type: $($_.ParameterType.FullName)  IsOut: $($_.IsOut)"
}

Write-Host ""
Write-Host "--- IMV_OK value ---"
$DefineType = $Asm.GetType("MVSDK_Net.IMVDefine")
$DefineType.GetField("IMV_OK").GetValue($null)

Write-Host ""
Write-Host "--- IMV_EInterfaceType enum values ---"
[Enum]::GetValues($Asm.GetType("MVSDK_Net.IMVDefine+IMV_EInterfaceType")) | ForEach-Object {
    Write-Host "  $_ = $([int]$_)"
}

Write-Host ""
Write-Host "--- IMV_ECameraType enum values ---"
[Enum]::GetValues($Asm.GetType("MVSDK_Net.IMVDefine+IMV_ECameraType")) | ForEach-Object {
    Write-Host "  $_ = $([int]$_)"
}
