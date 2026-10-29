# Offline UGS PvP bridge integration checks

This console project runs the production `UgsProfiles`, `UgsTicketStore`,
`UgsPvpWorker`, and `PartyStore`. It opens no HTTP listener and uses no cloud
credentials. Its `IUgsCloudStore` substitute implements ordered pagination,
Protected profile reads, real write-lock comparison, and a concurrent mailbox
touch that makes the first completion write fail with 409.

From the repository root:

```powershell
dotnet restore Server/AccountService/Tests/UgsBridge/UgsBridge.Tests.csproj --configfile Server/AccountService/NuGet.Config
dotnet run --project Server/AccountService/Tests/UgsBridge/UgsBridge.Tests.csproj -c Release --no-restore
```

No external NuGet packages are needed. In a restricted Codex filesystem where
the personal NuGet profile cannot be read, temporarily point the process's
`APPDATA` to a writable test directory during restore, then restore its original
value. Do not edit the user's real NuGet configuration.

The scenarios cover actual two-party matching and roster tickets; replay,
forgery, replacement, expiration and revision changes; latest custom-room
profiles; mailbox identity and epoch checks; response CAS retries without
repeating joins or minting tickets; request ID reuse; competing leases; corrupt
and absent Protected profiles; teammate readiness invalidation; and the complete
serialized response-slot size for 50 full rooms.

These checks do not verify Unity client behavior, NGO networking, real UGS REST
authorization, cloud deployment, Windows tasks, filesystem ACLs, or the actual
Rainyun machine. Those require separate validation.
