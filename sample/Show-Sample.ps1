Import-Module .\msgraphProxy\msgraphProxy.psd1 -Verbose -Force
# start-msgraphproxy will try and install a certificate to the certificate store on windows if it needs to be able to record then it needs to be trusted it is shipped with dev-proxy
start-MsGraphProxy
# Once it is started you are able to launch requests against the api like authentication 

$tenantId = "123"
$clientId = "123"
$clientSecret = "123"

$tokenBody = @{
	Grant_Type    = "client_credentials"
	Scope         = "https://graph.microsoft.com/.default"
	Client_Id     = $clientId
	Client_Secret = $clientSecret
}

$tokenResponse = Invoke-RestMethod -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" -Method POST -Body $tokenBody
$tokenResponse

$headers = @{
	Authorization = "Bearer $($tokenResponse.access_token)"
}

Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $headers

# Also does not have to be an actual token so you can skip auth like so:
$headers = @{ 
	Authorization = 'Bearer faketoken' 
}
Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users' -Headers $headers

$r = Stop-MsGraphProxy
$r.Recording.GraphMinimalPermissionsPlugin | Format-List
<# the recording of the proxy is returned from the Stop-MsGraphProxy so this script looks somthing like this
$r.Recording.GraphMinimalPermissionsPlugin | fl

errors             : {}
minimalPermissions : {User.ReadBasic.All}
permissionsType    : Application
requests           : {@{method=GET; requestUrl=/users}}
#>