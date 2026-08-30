#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
project="$repo_root/mySQLPunk.Desktop/mySQLPunk.Desktop.csproj"
version=""
runtime=""
configuration="Release"
output_root="$repo_root/dist/cross-platform"

usage() {
    printf '%s\n' \
        "Usage: $0 --version N.N.N[.N] --runtime <linux-x64|linux-arm64|osx-x64|osx-arm64> [--output DIR] [--configuration NAME]"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)
            version="${2:-}"
            shift 2
            ;;
        --runtime)
            runtime="${2:-}"
            shift 2
            ;;
        --output)
            output_root="${2:-}"
            shift 2
            ;;
        --configuration)
            configuration="${2:-}"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            printf 'Unknown argument: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    printf 'Version must be N.N.N or N.N.N.N. Received: %s\n' "$version" >&2
    exit 2
fi

case "$runtime" in
    linux-x64|linux-arm64)
        package_kind="linux"
        ;;
    osx-x64|osx-arm64)
        package_kind="macos"
        ;;
    *)
        printf 'Unsupported runtime: %s\n' "$runtime" >&2
        usage >&2
        exit 2
        ;;
esac

if [[ "$package_kind" == "macos" && "$(uname -s)" != "Darwin" ]]; then
    printf 'macOS .app packages must be assembled on macOS so codesign and archive metadata can be verified.\n' >&2
    exit 3
fi

mkdir -p "$output_root"
output_root=$(cd "$output_root" && pwd)
work_root=$(mktemp -d)
cleanup() {
    rm -rf -- "$work_root"
}
trap cleanup EXIT

publish_root="$work_root/publish"
dotnet publish "$project" \
    -c "$configuration" \
    -r "$runtime" \
    --self-contained true \
    -o "$publish_root" \
    -p:Version="$version" \
    -p:AssemblyVersion="$version" \
    -p:FileVersion="$version" \
    -p:InformationalVersion="$version" \
    -p:DebugType=None \
    -p:DebugSymbols=false

app_host="$publish_root/mySQLPunk"
if [[ ! -f "$app_host" ]]; then
    printf 'Published app host was not found: %s\n' "$app_host" >&2
    exit 4
fi
chmod +x "$app_host"

asset_base="mySQLPunk-$version-$runtime"
asset_path=""
if [[ "$package_kind" == "linux" ]]; then
    package_root="$work_root/$asset_base"
    mkdir -p "$package_root/app"
    cp -a "$publish_root/." "$package_root/app/"
    cp "$repo_root/packaging/linux/install.sh" "$package_root/install.sh"
    cp "$repo_root/packaging/linux/uninstall.sh" "$package_root/uninstall.sh"
    cp "$repo_root/packaging/linux/apply-update.sh" "$package_root/app/apply-update.sh"
    cp "$repo_root/packaging/linux/README.txt" "$package_root/README.txt"
    cp "$repo_root/LICENSE" "$package_root/LICENSE"
    cp "$repo_root/THIRD_PARTY_NOTICES.md" "$package_root/THIRD_PARTY_NOTICES.md"
    sed -i "s/@VERSION@/$version/g; s/@RUNTIME@/$runtime/g" \
        "$package_root/install.sh" "$package_root/uninstall.sh" "$package_root/README.txt"
    chmod +x "$package_root/install.sh" "$package_root/uninstall.sh" \
        "$package_root/app/mySQLPunk" "$package_root/app/apply-update.sh"

    asset_path="$output_root/$asset_base.tar.gz"
    temporary_asset="$work_root/$asset_base.tar.gz"
    tar -C "$work_root" -czf "$temporary_asset" "$asset_base"
    mv -f -- "$temporary_asset" "$asset_path"
else
    app_bundle="$work_root/mySQLPunk.app"
    contents_root="$app_bundle/Contents"
    mkdir -p "$contents_root/MacOS" "$contents_root/Resources"
    cp -a "$publish_root/." "$contents_root/MacOS/"
    cp "$repo_root/packaging/macos/apply-update.sh" "$contents_root/MacOS/apply-update.sh"
    cp "$repo_root/LICENSE" "$contents_root/Resources/LICENSE"
    cp "$repo_root/THIRD_PARTY_NOTICES.md" "$contents_root/Resources/THIRD_PARTY_NOTICES.md"

    short_version=$(printf '%s' "$version" | awk -F. '{ print $1 "." $2 "." $3 }')
    bundle_version=$(printf '%s' "$version" | tr -d '.')
    sed \
        -e "s/@SHORT_VERSION@/$short_version/g" \
        -e "s/@BUNDLE_VERSION@/$bundle_version/g" \
        -e "s/@RUNTIME@/$runtime/g" \
        "$repo_root/packaging/macos/Info.plist.in" > "$contents_root/Info.plist"
    chmod +x "$contents_root/MacOS/mySQLPunk" "$contents_root/MacOS/apply-update.sh"

    signing_identity="${MYSQLPUNK_MACOS_SIGN_IDENTITY:--}"
    if [[ "$signing_identity" == "-" ]]; then
        codesign --force --deep --sign - "$app_bundle"
    else
        codesign --force --deep --options runtime --timestamp --sign "$signing_identity" "$app_bundle"
    fi
    codesign --verify --deep --strict "$app_bundle"

    asset_path="$output_root/$asset_base.app.zip"
    temporary_asset="$work_root/$asset_base.app.zip"
    ditto -c -k --sequesterRsrc --keepParent "$app_bundle" "$temporary_asset"

    notary_profile="${MYSQLPUNK_MACOS_NOTARY_PROFILE:-}"
    if [[ -n "$notary_profile" ]]; then
        if [[ "$signing_identity" == "-" ]]; then
            printf 'MYSQLPUNK_MACOS_NOTARY_PROFILE requires a Developer ID signing identity.\n' >&2
            exit 5
        fi
        xcrun notarytool submit "$temporary_asset" --keychain-profile "$notary_profile" --wait
        xcrun stapler staple "$app_bundle"
        rm -f -- "$temporary_asset"
        ditto -c -k --sequesterRsrc --keepParent "$app_bundle" "$temporary_asset"
    fi
    mv -f -- "$temporary_asset" "$asset_path"
fi

if command -v sha256sum >/dev/null 2>&1; then
    hash=$(sha256sum "$asset_path" | awk '{ print $1 }')
else
    hash=$(shasum -a 256 "$asset_path" | awk '{ print $1 }')
fi
printf '%s  %s\n' "$hash" "$(basename "$asset_path")" > "$asset_path.sha256"
printf 'Package: %s\nSHA-256: %s\n' "$asset_path" "$hash"
