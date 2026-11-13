# -*- coding: utf-8 -*-
"""把客户端 cn.efefef.net 的纯 C# 协议文件逐字节复制到服务端契约层，并补一行同步来源标注。

保持字节级一致是刻意设计：KCP 是「线」上协议，任何一行差异都会导致两侧不通。

用法：
    python tools/sync-protocol.py                      # 用默认 Unity 包路径同步
    python tools/sync-protocol.py --package <包目录>    # 指定 cn.efefef.net 包目录
    python tools/sync-protocol.py --check              # 只校验不写入，漂移时退出码 1（可用于 CI）

Unity 包目录也可用环境变量 EF_NET_PACKAGE 指定，避免每次敲参数。
"""
import argparse
import os
import sys

FILES = ["Kcp.cs", "MessagePipe.cs", "NetOpcode.cs", "NetProtocolException.cs"]

MARKER = b" * SyncedFrom:"
ANCHOR = b" * ScriptVersion: 0.1"

# 默认值只为本机开箱可用；其它环境用 --package 或 EF_NET_PACKAGE 覆盖
DEFAULT_PACKAGE = r"D:/_Projects/Unity/EFramework_Package/EF_Unity/Packages/cn.efefef.net"
ENV_PACKAGE = "EF_NET_PACKAGE"

# 目标目录相对仓库根，随 checkout 位置变化，故从脚本自身位置推导
DST_RELATIVE = os.path.join("src", "EFramework.Net", "Protocol")


def repo_root():
    # 本脚本位于 <repo>/tools/ 下
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def add_marker(data, name):
    """在版本号行之后插入同步来源标注，保留原有 CRLF 换行。"""
    if MARKER in data:
        return data

    idx = data.find(ANCHOR)
    if idx < 0:
        raise SystemExit(f"anchor not found in {name}")

    line_end = data.find(b"\n", idx) + 1
    note = (
        MARKER
        + b"    EF_Unity/Packages/cn.efefef.net/Runtime/"
        + name.encode("utf-8")
        + b" (do not edit locally)\r\n"
    )
    return data[:line_end] + note + data[line_end:]


def main(argv):
    parser = argparse.ArgumentParser(
        description="Sync pure-C# protocol files from the Unity package cn.efefef.net.")
    parser.add_argument(
        "--package",
        default=os.environ.get(ENV_PACKAGE, DEFAULT_PACKAGE),
        help=f"cn.efefef.net package root (default: ${ENV_PACKAGE} or the built-in path)")
    parser.add_argument(
        "--dest",
        default=None,
        help=f"target folder (default: <repo>/{DST_RELATIVE})")
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify only, do not write; exit 1 when the copies differ (CI friendly)")
    args = parser.parse_args(argv)

    source_dir = os.path.join(args.package, "Runtime")
    dest_dir = args.dest or os.path.join(repo_root(), DST_RELATIVE)

    if not os.path.isdir(source_dir):
        print(f"[ERROR] protocol source not found: {source_dir}", file=sys.stderr)
        print(f"        pass --package <dir> or set {ENV_PACKAGE}", file=sys.stderr)
        return 1

    if not args.check:
        os.makedirs(dest_dir, exist_ok=True)

    updated = 0
    unchanged = 0
    drifted = []

    for name in FILES:
        src_path = os.path.join(source_dir, name)
        dst_path = os.path.join(dest_dir, name)

        if not os.path.isfile(src_path):
            print(f"[ERROR] missing source file: {src_path}", file=sys.stderr)
            return 1

        with open(src_path, "rb") as f:
            expected = add_marker(f.read(), name)

        current = None
        if os.path.isfile(dst_path):
            with open(dst_path, "rb") as f:
                current = f.read()

        if current == expected:
            unchanged += 1
            print(f"  unchanged  {name}")
            continue

        if args.check:
            drifted.append(name)
            print(f"  DRIFT      {name}")
            continue

        with open(dst_path, "wb") as f:
            f.write(expected)
        updated += 1
        print(f"  updated    {name}  ({len(expected)} bytes)")

    print(f"\nsource: {source_dir}")
    print(f"dest  : {dest_dir}")

    if args.check:
        if drifted:
            print(f"[FAIL] {len(drifted)} file(s) out of sync: {', '.join(drifted)}")
            return 1
        print(f"[OK] all {len(FILES)} file(s) in sync")
        return 0

    print(f"[DONE] updated={updated}, unchanged={unchanged}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
