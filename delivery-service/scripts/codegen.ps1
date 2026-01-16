param(
  [string]$Proto = "delivery-service/proto/delivery/v1/delivery.proto"
)

$ErrorActionPreference = "Stop"

$protoc = Join-Path $env:USERPROFILE "protoc/bin/protoc.exe"
if (-not (Test-Path $protoc)) {
  throw "protoc not found at $protoc"
}

$gwV1 = Join-Path $env:GOPATH "pkg/mod/github.com/grpc-ecosystem/grpc-gateway@v1.16.0"
$gwV2 = Join-Path $env:GOPATH "pkg/mod/github.com/grpc-ecosystem/grpc-gateway/v2@v2.27.4"
$val = Join-Path $env:GOPATH "pkg/mod/github.com/envoyproxy/protoc-gen-validate@v1.3.0"

if (-not (Test-Path $gwV1)) { throw "grpc-gateway v1 not found at $gwV1" }
if (-not (Test-Path $gwV2)) { throw "grpc-gateway v2 not found at $gwV2" }
if (-not (Test-Path $val)) { throw "protoc-gen-validate not found at $val" }

$env:PATH = "$env:GOPATH\bin;$env:PATH"

New-Item -ItemType Directory -Force -Path delivery-service/gen/go | Out-Null
New-Item -ItemType Directory -Force -Path delivery-service/gen/openapiv2 | Out-Null

& $protoc `
  -I delivery-service/proto `
  -I "$gwV1/third_party/googleapis" `
  -I "$gwV2" `
  -I "$val" `
  --go_out=delivery-service/gen/go --go_opt=paths=source_relative `
  --go-grpc_out=delivery-service/gen/go --go-grpc_opt=paths=source_relative `
  --grpc-gateway_out=delivery-service/gen/go --grpc-gateway_opt=paths=source_relative,generate_unbound_methods=true `
  --openapiv2_out=delivery-service/gen/openapiv2 `
  --validate_out=lang=go,paths=source_relative:delivery-service/gen/go `
  $Proto
