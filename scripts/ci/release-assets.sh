#!/usr/bin/env bash
# 把一个版本的全部发布物装进 release/：每个 RID 一个 .app 的 zip，外加校验和与 manifest。
#
#   scripts/ci/release-assets.sh 1.3.0            # → release/Conclave-1.3.0-osx-arm64.zip 等
#   RIDS="osx-arm64" scripts/ci/release-assets.sh 1.3.0
#
# 逻辑放在脚本里而不是写进 workflow 的 run: 块，是为了能在本机跑一遍再推 —— GitHub Actions
# 只能推上去才知道对不对，而这里每一步都能本地复现。workflow 只负责调用它。
#
# manifest.json 是日后 app 内更新器要读的东西：版本、提交、每个包的 sha256。
# 校验和写在这里、由 CI 生成，app 下载后对一次 —— 信任锚是 GitHub Release 本身。
set -euo pipefail

cd "$(dirname "$0")/../.."

VERSION=${1:-${VERSION:-}}
[ -n "$VERSION" ] || { echo "用法：$0 <版本号，不带 v>" >&2; exit 1; }
case "$VERSION" in v*) echo "版本号不要带 v（收到 '$VERSION'）" >&2; exit 1 ;; esac

RIDS=${RIDS:-"osx-arm64 osx-x64"}
OUT=release
rm -rf "$OUT"
mkdir -p "$OUT"

for rid in $RIDS; do
  echo "==> 打包 ${rid}"
  RID="$rid" VERSION="$VERSION" CONFIG=Release scripts/package-macos.sh

  # 两种包都出，因为它们的<b>隔离标记</b>行为不同（实测）：
  #   tar -xzf   → 解出的 .app 完全没有 com.apple.quarantine，Gatekeeper 不拦
  #   ditto/unzip → 隔离标记会传染给解出的 .app，于是要么在系统设置里放行、
  #                 要么 xattr -dr
  # 所以 NOTES 里推荐 tar 那条路；zip 留着，因为 app 内更新器下载的就是它
  # （它自己写文件、没有隔离标记，而且解完还会 xattr -dr 兜一层）。
  zip="${OUT}/Conclave-${VERSION}-${rid}.zip"
  tarball="${OUT}/Conclave-${VERSION}-${rid}.tar.gz"
  # 先把打包机上的扩展属性全清掉，再用 --norsrc 不写 ._ 文件。不然 zip 里会带着
  # 构建机的 com.apple.provenance / quarantine，解开时原样落到对方文件上 ——
  # 实测第一版 zip 里就有 ._Info.plist、._appsettings.json 这些 AppleDouble 条目。
  # ditto 而不是 zip：保住权限位，解开还是一个能双击的 bundle。
  # 打包时<b>必须保留</b>扩展属性，不能再用 --norsrc：codesign 对 Contents/MacOS 里那些
  # 非 Mach-O 文件（.NET 的托管 dll）把签名存在 com.apple.cs.CodeDirectory 扩展属性里，
  # 丢掉它们，解出来的 bundle 就是 "code object is not signed at all（In subcomponent:
  # …dll）"，Gatekeeper 照样报「已损坏」。构建机自己的 provenance/quarantine 由
  # package-macos.sh 在<b>签名之前</b> xattr -cr 掉，所以这里留下的只有签名本身。
  # 代价是 zip 里有一批 ._ 的 AppleDouble 条目 —— 那正是扩展属性的载体，不是垃圾。
  ditto -c -k --keepParent dist/Conclave.app "$zip"
  # -C 到 dist：tar 里要的是 Conclave.app 本身，不是 dist/Conclave.app 这条路径
  tar -czf "$tarball" -C dist Conclave.app
  echo "    → ${zip} ($(du -h "$zip" | cut -f1))"
  echo "    → ${tarball} ($(du -h "$tarball" | cut -f1))"
done

echo "==> 校验和与 manifest"
( cd "$OUT" && shasum -a 256 ./*.zip ./*.tar.gz > SHA256SUMS )

python3 - "$VERSION" "$OUT" <<'PY'
import hashlib, json, os, subprocess, sys, datetime
version, out = sys.argv[1], sys.argv[2]
commit = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
assets = []
for name in sorted(os.listdir(out)):
    if not name.endswith(".zip"):
        continue
    path = os.path.join(out, name)
    with open(path, "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    rid = name[len(f"Conclave-{version}-"):-len(".zip")]
    assets.append({"rid": rid, "file": name, "sha256": digest, "bytes": os.path.getsize(path)})
manifest = {
    "name": "Conclave",
    "version": version,
    "tag": f"v{version}",
    "commit": commit,
    "builtAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "assets": assets,
}
with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)
    f.write("\n")
print(json.dumps(manifest, ensure_ascii=False, indent=2))
PY

# Release 页面上方的安装说明。包没签名，不写这几行的话同事双击只会看到「已损坏」。
cat > "${OUT}/NOTES.md" <<MD
## 安装

包<b>没有 Apple 公证</b>（那需要付费的 Developer ID），所以下载后 macOS 默认会拦。
用 \`tar\` 解压可以完全绕开这件事 —— 实测 \`tar\` 不会把下载来的隔离标记传染给解出的
\`.app\`，而 \`unzip\` / 双击（归档工具）会：

\`\`\`bash
# 选自己的芯片：Apple Silicon 用 osx-arm64，Intel 用 osx-x64
tar -xzf Conclave-${VERSION}-osx-arm64.tar.gz -C /Applications
open /Applications/Conclave.app
\`\`\`

用 zip 的话（双击解压也是这条路）多一步去隔离标记：

\`\`\`bash
unzip Conclave-${VERSION}-osx-arm64.zip -d /Applications
xattr -dr com.apple.quarantine /Applications/Conclave.app
open /Applications/Conclave.app
\`\`\`

要常驻后台（关掉窗口继续跑轮询与评审）：

\`\`\`bash
cp scripts/com.liontravel.conclave.plist ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/com.liontravel.conclave.plist
\`\`\`

⚠️ **整个 mesh 要一起升级** —— 心跳协议有变动时新旧节点会互相验签失败。

校验：\`shasum -a 256 -c SHA256SUMS\`（\`manifest.json\` 里是同一组值）。
MD

echo
echo "✅ ${OUT}/"
ls -la "$OUT"
