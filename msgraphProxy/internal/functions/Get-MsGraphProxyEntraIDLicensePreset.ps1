function Get-MsGraphProxyEntraIDLicensePreset {
	<#
	.SYNOPSIS
		Builds a graphSchemaMockPlugin subscribedSkus entry for a named Entra ID
		license tier.

	.DESCRIPTION
		Maester's Get-MtLicenseInformation (and anything else that inspects
		subscribedSkus the same way) detects the tenant's Entra ID license by
		checking for specific servicePlanId GUIDs in priority order: P2, then
		Governance, then P1, falling back to Free if none match. This returns a
		subscribedSkus entry carrying the right GUID for the requested tier, for
		Start-MsGraphProxy's -EntraIDLicense to feed into
		New-MsGraphProxyCIConfigFile -SubscribedSkus.

	.PARAMETER License
		The Entra ID license tier to build a preset for.

	.EXAMPLE
		PS C:\> Get-MsGraphProxyEntraIDLicensePreset -License P2

		Returns a subscribedSkus entry for Entra ID P2.
	#>
	[CmdletBinding()]
	param (
		[Parameter(Mandatory)]
		[ValidateSet('Free', 'P1', 'P2', 'Governance')]
		[string]
		$License
	)

	# Real Microsoft Entra ID service plan GUIDs - see
	# https://learn.microsoft.com/entra/identity/users/licensing-service-plan-reference
	switch ($License) {
		'P2' {
			[pscustomobject]@{
				skuPartNumber    = 'AAD_PREMIUM_P2'
				capabilityStatus = 'Enabled'
				servicePlans     = @(
					[pscustomobject]@{
						servicePlanId   = 'eec0eb4f-6444-4f95-aba0-50c24d67f998'
						servicePlanName = 'AAD_PREMIUM_P2'
					}
				)
			}
		}
		'Governance' {
			[pscustomobject]@{
				skuPartNumber    = 'AAD_PREMIUM_GOVERNANCE'
				capabilityStatus = 'Enabled'
				servicePlans     = @(
					[pscustomobject]@{
						servicePlanId   = 'e866a266-3cff-43a3-acca-0c90a7e00c8b'
						servicePlanName = 'Entra_Identity_Governance'
					}
				)
			}
		}
		'P1' {
			[pscustomobject]@{
				skuPartNumber    = 'AAD_PREMIUM'
				capabilityStatus = 'Enabled'
				servicePlans     = @(
					[pscustomobject]@{
						servicePlanId   = '41781fb2-bc02-4b7c-bd55-b576c07bb09d'
						servicePlanName = 'AAD_PREMIUM'
					}
				)
			}
		}
		'Free' {
			# A real tenant on Entra ID Free still has *some* subscribedSkus
			# entry (Exchange, Teams, whatever else) - just none of them carry
			# a P1/P2/Governance service plan, which is what actually makes
			# Get-MtLicenseInformation fall through to 'Free'.
			[pscustomobject]@{
				skuPartNumber    = 'EXCHANGESTANDARD'
				capabilityStatus = 'Enabled'
				servicePlans     = @(
					[pscustomobject]@{
						servicePlanId   = '9aaf7827-d63c-4b61-89c3-182f06f82e5c'
						servicePlanName = 'EXCHANGE_S_STANDARD'
					}
				)
			}
		}
	}
}
