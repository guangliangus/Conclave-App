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

# 刻意不用 PublishSingleFile。它默认把原生库留在单文件之外
# （IncludeNativeLibrariesForSelfExtract 默认 false），于是只拷可执行文件会漏掉
# libe_sqlite3.dylib，装好的 .app 一启动就 DllNotFoundException。
# .app 本来就是个目录，在里面再套单文件没有好处。
echo "==> 发布（${RID} / ${CONFIG} / self-contained）"
dotnet publish src/Conclave.App \
  -c "${CONFIG}" -r "${RID}" \
  --self-contained true \
  -p:DebugType=none \
  -p:Version="${VERSION}" \
  -o dist/publish \
  --nologo

echo "==> 组装 bundle"
# 整个发布目录都进去：运行时、原生库、appsettings.json 一个不能少
cp -R dist/publish/. "${APP}/Contents/MacOS/"

# 评审 skill 挪去 Resources。它不能留在 Contents/MacOS 旁边：codesign --deep
# 会把 review-plugin/.claude-plugin 这个点目录当成嵌套 bundle 去签，报
#   bundle format unrecognized, invalid, or unsuitable
#   In subcomponent: .../Contents/MacOS/review-plugin/.claude-plugin
# 整个打包就挂在这一步（实测最小复现：同一棵树放 Resources 下签名与验签都过，
# 放 MacOS 下两样都失败）。ReviewSkillDeployer 两个位置都会找。
if [ -d "${APP}/Contents/MacOS/review-plugin" ]; then
  mv "${APP}/Contents/MacOS/review-plugin" "${APP}/Contents/Resources/review-plugin"
else
  echo "!! dist/publish 里没有 review-plugin —— 评审会拿不到 /az-pr-review" >&2
  exit 1
fi

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
  <!-- mesh 靠 UDP 多播发现邻居，而这版 macOS 把本地网络访问归到隐私授权下管。
       不写这句的话系统弹的授权框没有任何理由说明，用户更容易点拒绝 ——
       而拒绝之后的表现是「一条心跳都收不到」，跟多播被交换机拦掉一模一样，极难排查。 -->
  <key>NSLocalNetworkUsageDescription</key>
  <string>Conclave 用本地网络发现同事的节点，把 code review 任务分给空闲的机器。</string>
  <!-- 菜单栏常驻应用：不占 Dock、不进 Cmd+Tab。运行时也设了 ShowInDock=false，
       这里再写一遍是为了连启动瞬间都不在 Dock 里闪 —— Info.plist 比托管代码先被读。 -->
  <key>LSUIElement</key>              <true/>
  <key>LSApplicationCategoryType</key> <string>public.app-category.developer-tools</string>
  <!-- conclave:// 深链。飞书「开始评审」通知上那个「在 Conclave 里打开」按钮靠它唤起本机。
       协议名跟 Conclave.Application.DeepLink.Scheme 必须一致 —— 对不上的表现是
       「点了按钮没反应」：系统找不到能处理的应用，既不报错也不提示，
       跟「压根没装 Conclave」一模一样。
       注册发生在 Launch Services 扫到这个 .app 的时候，所以换了路径或改了这一段之后，
       要么重新打开一次 .app，要么 lsregister -f 它。 -->
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key>     <string>${BUNDLE_ID}.review</string>
      <key>CFBundleTypeRole</key>    <string>Viewer</string>
      <key>CFBundleURLSchemes</key>  <array><string>conclave</string></array>
    </dict>
  </array>
</dict>
</plist>
PLIST

rm -rf dist/publish

# 自检：只看文件在不在是不够的 —— 缺原生库时文件全在，一跑就炸。
# ── 签名 ─────────────────────────────────────────────────────────────
# 必须签，而且必须在 bundle 组装完（Info.plist 与 .icns 都写好）之后签。
#
# 为什么：.NET SDK 会给 apphost 打一个 ad-hoc 签名，而那个签名<b>声明自己属于一个
# bundle</b>（要求存在 _CodeSignature/CodeResources）。我们是 publish 完再手工组装
# bundle 的，从不在 bundle 级签名，于是签名<b>无效</b>而不是「没签名」：
#
#   codesign -v --deep --strict Conclave.app
#   → code has no resources but signature indicates they must be present
#
# Gatekeeper 对无效签名的说法是「已损坏，你应该将它移到废纸篓」—— 没有任何出路，
# 比「无法验证开发者」（那个至少能在系统设置里放行）糟得多。实测同事下载后就撞上这个。
#
# 传了 CODESIGN_IDENTITY 就用它签，没传就 ad-hoc（"-"），至少让签名自洽。
#
# 两种证书走不同的参数，按身份名字分：
#
#   Developer ID —— 要发给别人的那条路。--options runtime（强化运行时）是公证的
#   硬要求，--timestamp 去 Apple 的时间戳服务器盖章，公证同样要求。
#
#   自签证书 —— 只为让 TCC 身份稳定（ad-hoc 的「指定要求」是 cdhash，见
#   codesign -d -r-，每次构建都变，用户对弹窗的选择于是每版作废一次）。这条路
#   两个参数都不能要：自签证书拿不到可信时间戳源，--timestamp 会失败；而强化运行时
#   对没有公证的包没有意义，还可能让 .NET 的运行时起不来。它解决不了 Gatekeeper ——
#   在别人机器上自签和 ad-hoc 一样不被信任，所以别拿它当分发方案。
# 先清干净再签，顺序不能反。构建机上的 com.apple.provenance / quarantine 会跟着
# 扩展属性一路进包，而<b>签名本身也存在扩展属性里</b>（托管 dll 那些）——
# 反过来先签后清，等于把刚签好的签名擦掉，实测就是这么发出一个「已损坏」的包的。
echo "==> 清扩展属性"
xattr -cr "${APP}"

echo "==> 签名（${CODESIGN_IDENTITY:-ad-hoc}）"
if [ -n "${CODESIGN_IDENTITY:-}" ]; then
  case "${CODESIGN_IDENTITY}" in
    "Developer ID"*)
      codesign --force --deep --timestamp --options runtime \
        --sign "${CODESIGN_IDENTITY}" "${APP}" ;;
    *)
      codesign --force --deep --timestamp=none \
        --sign "${CODESIGN_IDENTITY}" "${APP}" ;;
  esac
else
  codesign --force --deep --sign - "${APP}"
fi

# 签完立刻验。不验的话，无效签名照样能打进包发出去 —— 这次就是这么发出去的。
echo "==> 校验签名"
if ! codesign -v --deep --strict "${APP}" 2>dist/codesign.err; then
  echo "❌ 签名无效，不发这个包：" >&2
  sed -n '1,10p' dist/codesign.err >&2
  exit 1
fi
rm -f dist/codesign.err

# 交叉打包（在 arm64 机器上出 osx-x64 的包）时，自检要靠 Rosetta 才跑得起来。
# GitHub 的 macOS runner 带 Rosetta，所以 CI 上两个架构都真跑；本机没装的话只跳过自检、
# 不让打包失败 —— 但要说出来，别让人以为验过了。
HOST_ARCH=$(uname -m)
case "${RID}" in
  osx-x64)   WANT_ARCH=x86_64 ;;
  osx-arm64) WANT_ARCH=arm64 ;;
  *)         WANT_ARCH=${HOST_ARCH} ;;
esac

if [ "${WANT_ARCH}" != "${HOST_ARCH}" ] && ! arch -"${WANT_ARCH}" /usr/bin/true 2>/dev/null; then
  echo "==> 跳过自检：本机是 ${HOST_ARCH}，没有 Rosetta 跑不了 ${RID} 的二进制"
else
  echo "==> 自检（跑一次 report）"
  if ! "${APP}/Contents/MacOS/conclave" report >/dev/null 2>dist/smoke.err; then
    echo "❌ bundle 起不来：" >&2
    # 要 head 不要 tail：.NET 未处理异常的<b>第一行</b>才是消息（"SQLite Error 14: unable to
    # open database file"），后面全是栈帧。原先 tail -5 只印栈尾，CI 上失败了也看不出原因，
    # 得在本机复刻环境才能诊断 —— 这条自检的价值一半在于它说得清。
    sed -n '1,40p' dist/smoke.err >&2
    exit 1
  fi
  rm -f dist/smoke.err
fi

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
