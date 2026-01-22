Sub Main(Parm As Object)
    ' Query HomeSeer to find thermostat device references
    Dim devEnum As Object = hs.GetDeviceEnumerator()
    
    hs.WriteLog("FindRefs", "=== Searching for Thermostat Device References ===")
    
    Do While devEnum.GetNext()
        Dim dev As Object = devEnum.GetCurrent()
        If dev Is Nothing Then Continue Do
        
        Dim devName As String = dev.get_Name(hs)
        Dim devLocation As String = dev.get_Location(hs)
        Dim devRef As Integer = dev.get_Ref(hs)
        
        ' Look for Status devices (root thermostat devices)
        If devName = "Status" Then
            hs.WriteLog("FindRefs", "Found thermostat: " & devLocation & " = Ref " & devRef)
        End If
    Loop
    
    hs.WriteLog("FindRefs", "=== Search Complete ===")
End Sub
