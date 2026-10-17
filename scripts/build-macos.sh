#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
build_label="${ASIDE_BUILD_LABEL:-1.1.4}"
mkdir -p .build/arm64 .build/x86_64 dist/Aside.app/Contents/MacOS dist/Aside.app/Contents/Resources
sdk="${SDKROOT:-$(xcrun --show-sdk-path)}"
for arch in arm64 x86_64; do
    swiftc -swift-version 5 -O -target "$arch-apple-macosx13.0" -sdk "$sdk" \
        -parse-as-library -emit-module -emit-library -static -module-name QuietPinCore \
        Sources/QuietPinCore/Notebook.swift -emit-module-path ".build/$arch/QuietPinCore.swiftmodule" \
        -o ".build/$arch/libQuietPinCore.a"
    swiftc -swift-version 5 -O -target "$arch-apple-macosx13.0" -sdk "$sdk" -parse-as-library \
        -I ".build/$arch" -L ".build/$arch" -lQuietPinCore Sources/QuietPin/*.swift \
        -o ".build/$arch/QuietPin"
done
lipo -create .build/arm64/QuietPin .build/x86_64/QuietPin -output dist/Aside.app/Contents/MacOS/Aside
cp packaging/Info.plist dist/Aside.app/Contents/Info.plist
cp assets/GitHub_Invertocat_Black.png dist/Aside.app/Contents/Resources/GitHub_Invertocat_Black.png
cp assets/GitHub_Invertocat_White.png dist/Aside.app/Contents/Resources/GitHub_Invertocat_White.png
swift scripts/make-icon.swift
iconutil -c icns .build/Aside.iconset -o dist/Aside.app/Contents/Resources/Aside.icns
codesign --force --sign - dist/Aside.app
codesign --verify --deep --strict dist/Aside.app
stage="$(mktemp -d "${TMPDIR:-/tmp}/aside-dmg.XXXXXX")"
ditto dist/Aside.app "$stage/Aside.app"
ln -s /Applications "$stage/Applications"
cp packaging/安装说明.txt "$stage/安装说明.txt"
hdiutil create -volname Aside -srcfolder "$stage" -ov -format UDZO "dist/Aside-${build_label}-macOS-universal.dmg"
printf 'Built: %s/dist/Aside.app\nBuilt: %s/dist/Aside-%s-macOS-universal.dmg\n' "$PWD" "$PWD" "$build_label"
