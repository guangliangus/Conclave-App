#!/usr/bin/env bash
# 打成一个可以拷给同事的 Conclave.app。
#
# 自用：跑完直接 open dist/Conclave.app 即可。若被 Gatekeeper 拦，
#   xattr -dr com.apple.quarantine dist/Conclave.app
# 发给别人：必须签名 + 公证，否则对方双击只会看到「已损坏」。见文末。
set -euo pipefail

cd "$(dirname "$0")/.."

RID=${RID:-osx-arm64}
CONFIG=${CONFIG:-Release}
VERSION=${VERSION:-0.1.0}
APP=dist/Conclave.app
BUNDLE_ID=com.liontravel.conclave

echo "==> 清理"
rm -rf dist
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"

echo "==> 发布（${RID} / ${CONFIG} / self-contained 单文件）"
dotnet publish src/Conclave.App \
  -c "${CONFIG}" -r "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:DebugType=none \
  -p:Version="${VERSION}" \
  -o dist/publish \
  --nologo

echo "==> 组装 bundle"
cp dist/publish/conclave "${APP}/Contents/MacOS/"
# 单文件发布后配置文件仍是独立的一份，要跟可执行文件放在一起：
# 程序读的是 AppContext.BaseDirectory 下的 appsettings.json
cp dist/publish/appsettings.json "${APP}/Contents/MacOS/"

echo "==> 生成 .icns"
ICONSET=dist/Conclave.iconset
mkdir -p "${ICONSET}"
SRC=src/Conclave.App/Assets/conclave.png
for spec in "16 icon_16x16" "32 icon_16x16@2x" "32 icon_32x32" "64 icon_32x32@2x" \
            "128 icon_128x128" "256 icon_128x128@2x" "256 icon_256x256" \
            "512 icon_256x256@2x" "512 icon_512x512" "1024 icon_512x512@2x"; do
  size=${spec%% *}
  name=${spec##* }
  sips -z "${size}" "${size}" "${SRC}" --out "${ICONSET}/${name}.png" >/dev/null
done
iconutil -c icns "${ICONSET}" -o "${APP}/Contents/Resources/Conclave.icns"
rm -rf "${ICONSET}"

echo "==> 写 Info.plist"
cat > "${APP}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>              <string>Conclave</string>
  <key>CFBundleDisplayName</key>       <string>Conclave</string>
  <key>CFBundleIdentifier</key>        <string>${BUNDLE_ID}</string>
  <key>CFBundleExecutable</key>        <string>conclave</string>
  <key>CFBundleIconFile</key>          <string>Conclave</string>
  <key>CFBundlePackageType</key>       <string>APPL</string>
  <key>CFBundleVersion</key>           <string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key>    <string>12.0</string>
  <key>NSHighResolutionCapable</key>   <true/>
  <key>LSApplicationCategoryType</key> <string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

rm -rf dist/publish
SIZE=$(du -sh "${APP}" | cut -f1)
echo
echo "✅ ${APP}（${SIZE}）"
echo
echo "自用："
echo "  xattr -dr com.apple.quarantine ${APP} && open ${APP}"
echo
echo "发给同事（需要 Apple Developer 账号）："
echo "  codesign --deep --force --options runtime --timestamp \\"
echo "    --sign \"Developer ID Application: <你的名字> (<TEAMID>)\" ${APP}"
echo "  ditto -c -k --keepParent ${APP} dist/Conclave.zip"
echo "  xcrun notarytool submit dist/Conclave.zip --keychain-profile <profile> --wait"
echo "  xcrun stapler staple ${APP}"
