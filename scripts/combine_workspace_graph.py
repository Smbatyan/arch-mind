"""
combine_workspace_graph.py

Merge per-repo graphify graphs into one workspace-level combined graph,
colored by repo. No rescanning — reads existing graph.json files only.

Usage:
    python3 combine_workspace_graph.py <workspace_dir>
    python3 combine_workspace_graph.py /var/archmind/workspaces/<workspace-uuid>

Outputs:
    <workspace_dir>/graphify-out/combined_graph.html   - open in any browser
    <workspace_dir>/graphify-out/combined_graph.json   - merged node-link data
"""

import argparse
import json
import sys
from pathlib import Path


REPO_COLORS = [
    "#4e79a7",  # blue
    "#f28e2b",  # orange
    "#59a14f",  # green
    "#e15759",  # red
    "#76b7b2",  # teal
    "#edc948",  # yellow
    "#b07aa1",  # purple
    "#ff9da7",  # pink
    "#9c755f",  # brown
    "#bab0ac",  # gray
]


def get_repo_name(repo_dir: Path) -> str:
    """Infer a human-readable name for a repo directory."""
    # 1. package.json "name"
    pkg = repo_dir / "package.json"
    if pkg.exists():
        try:
            d = json.loads(pkg.read_text(encoding="utf-8"))
            name = d.get("name", "").strip()
            if name:
                return name
        except Exception:
            pass

    # 2. *.slnx / *.sln stem
    for pattern in ("*.slnx", "*.sln"):
        for f in repo_dir.glob(pattern):
            return f.stem

    # 3. pyproject.toml [tool.poetry] name
    pyproject = repo_dir / "pyproject.toml"
    if pyproject.exists():
        for line in pyproject.read_text(encoding="utf-8").splitlines():
            if line.strip().startswith("name"):
                parts = line.split("=", 1)
                if len(parts) == 2:
                    return parts[1].strip().strip('"').strip("'")

    # 4. CLAUDE.md first heading
    claude_md = repo_dir / "CLAUDE.md"
    if claude_md.exists():
        for line in claude_md.read_text(encoding="utf-8").splitlines():
            if line.startswith("# "):
                return line[2:].strip()

    # 5. Fallback: UUID prefix
    return repo_dir.name[:8]


_NEIGHBOR_FILTER_JS = r"""
<script>
// Double-click filter: dbl-click node → hide all except node + neighbors.
// Dbl-click empty space → restore everything.
(function() {
  if (typeof network === 'undefined' || typeof nodesDS === 'undefined' || typeof edgesDS === 'undefined') return;

  let filtered = false;

  function restore() {
    const nodeUpdates = nodesDS.getIds().map(id => ({ id, hidden: false }));
    const edgeUpdates = edgesDS.getIds().map(id => ({ id, hidden: false }));
    nodesDS.update(nodeUpdates);
    edgesDS.update(edgeUpdates);
    filtered = false;
  }

  function focusOn(nodeId) {
    const keep = new Set([nodeId]);
    network.getConnectedNodes(nodeId).forEach(n => keep.add(n));

    const nodeUpdates = nodesDS.getIds().map(id => ({ id, hidden: !keep.has(id) }));
    const edgeUpdates = edgesDS.get().map(e => ({
      id: e.id,
      hidden: !(keep.has(e.from) && keep.has(e.to)),
    }));
    nodesDS.update(nodeUpdates);
    edgesDS.update(edgeUpdates);
    filtered = true;
  }

  network.on('doubleClick', params => {
    if (params.nodes && params.nodes.length > 0) {
      focusOn(params.nodes[0]);
    } else if (filtered) {
      restore();
    }
  });

  // Visual hint
  const hint = document.createElement('div');
  hint.style.cssText = 'position:fixed;bottom:10px;left:10px;background:rgba(0,0,0,0.7);color:#fff;padding:6px 10px;font:12px sans-serif;border-radius:4px;pointer-events:none;z-index:9999';
  hint.textContent = 'Double-click node = isolate neighbors · Double-click empty = restore';
  document.body.appendChild(hint);
})();
</script>
"""


def _inject_neighbor_filter(html_path: Path) -> None:
    """Append vis.js double-click filter script before </body>."""
    html = html_path.read_text(encoding="utf-8")
    if "</body>" in html:
        html = html.replace("</body>", _NEIGHBOR_FILTER_JS + "\n</body>")
    else:
        html += _NEIGHBOR_FILTER_JS
    html_path.write_text(html, encoding="utf-8")


def main(workspace_dir: str) -> None:
    ws = Path(workspace_dir).resolve()
    repos_dir = ws / "repos"

    if not repos_dir.exists():
        print(f"ERROR: no repos/ dir found in {ws}", file=sys.stderr)
        sys.exit(1)

    # Collect repos that have a graph.json
    repo_graphs = []
    for repo_dir in sorted(repos_dir.iterdir()):
        if not repo_dir.is_dir():
            continue
        graph_path = repo_dir / "graphify-out" / "graph.json"
        if not graph_path.exists():
            print(f"  skip {repo_dir.name[:8]}… — no graph.json yet")
            continue
        name = get_repo_name(repo_dir)
        try:
            data = json.loads(graph_path.read_text(encoding="utf-8"))
        except Exception as e:
            print(f"  skip {repo_dir.name[:8]}… — parse error: {e}", file=sys.stderr)
            continue
        repo_graphs.append((repo_dir.name, name, data))
        n_nodes = len(data.get("nodes", []))
        n_edges = len(data.get("links", data.get("edges", [])))
        print(f"  loaded  {name:25s}  ({n_nodes:5d} nodes, {n_edges:5d} edges)")

    if not repo_graphs:
        print("No graphs found — run graphify on each repo first.", file=sys.stderr)
        sys.exit(1)

    # -------------------------------------------------------------------------
    # Build merged graph.  Each repo becomes a "community" so graphify's
    # to_html colours it uniformly.
    # -------------------------------------------------------------------------
    try:
        import networkx as nx
        from networkx.readwrite import json_graph
        from graphify.export import to_html, to_json  # type: ignore[import]
    except ImportError as exc:
        print(f"ERROR: required package missing — {exc}", file=sys.stderr)
        print("Install with:  pip install graphifyy networkx", file=sys.stderr)
        sys.exit(1)

    G_combined = nx.Graph()
    communities: dict[int, list[str]] = {}

    for idx, (repo_id, repo_name, data) in enumerate(repo_graphs):
        prefix = repo_id[:8] + "__"

        raw_nodes = data.get("nodes", [])
        raw_edges = data.get("links", data.get("edges", []))

        community_nodes: list[str] = []

        for node in raw_nodes:
            old_id = node.get("id", "")
            new_id = prefix + old_id
            attrs = {k: v for k, v in node.items() if k != "id"}
            attrs["repo"] = repo_name
            attrs["repo_id"] = repo_id
            G_combined.add_node(new_id, **attrs)
            community_nodes.append(new_id)

        for edge in raw_edges:
            # node-link format uses "source"/"target" which may be id strings
            # or integer indices depending on networkx version / export flags.
            src_raw = edge.get("source", "")
            tgt_raw = edge.get("target", "")

            # Handle integer-index form: look up by position in raw_nodes
            if isinstance(src_raw, int):
                src_raw = raw_nodes[src_raw].get("id", str(src_raw)) if src_raw < len(raw_nodes) else str(src_raw)
            if isinstance(tgt_raw, int):
                tgt_raw = raw_nodes[tgt_raw].get("id", str(tgt_raw)) if tgt_raw < len(raw_nodes) else str(tgt_raw)

            new_src = prefix + src_raw
            new_tgt = prefix + tgt_raw

            if G_combined.has_node(new_src) and G_combined.has_node(new_tgt):
                edge_attrs = {k: v for k, v in edge.items() if k not in ("source", "target")}
                G_combined.add_edge(new_src, new_tgt, **edge_attrs)

        communities[idx] = community_nodes

    community_labels = {idx: name for idx, (_, name, _) in enumerate(repo_graphs)}

    total_nodes = G_combined.number_of_nodes()
    total_edges = G_combined.number_of_edges()
    print(f"\nCombined: {total_nodes} nodes  {total_edges} edges  {len(repo_graphs)} repos")

    # Output directory
    out_dir = ws / "graphify-out"
    out_dir.mkdir(exist_ok=True)

    # HTML visualisation — lift graphify's hard 5 000-node cap for combined
    # workspace views; the repo-colour overview is the whole point.
    import graphify.export as _gx_export
    _gx_export.MAX_NODES_FOR_VIZ = max(total_nodes + 1, _gx_export.MAX_NODES_FOR_VIZ)
    if total_nodes > 5000:
        print(
            f"NOTE: {total_nodes} nodes — large graph, browser may be slow. "
            "Use combined_graph.json for programmatic queries."
        )
    html_path = out_dir / "combined_graph.html"
    to_html(G_combined, communities, str(html_path), community_labels=community_labels)
    _inject_neighbor_filter(html_path)
    print(f"Written: {html_path}")

    # Combined graph.json for future queries
    json_path = out_dir / "combined_graph.json"
    to_json(G_combined, communities, str(json_path))
    print(f"Written: {json_path}")

    # Colour legend to stdout
    print("\nRepo colour legend:")
    for idx, (_, name, _) in enumerate(repo_graphs):
        color = REPO_COLORS[idx % len(REPO_COLORS)]
        print(f"  {color}  {name}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Merge per-repo graphify graphs → combined repo-coloured HTML"
    )
    parser.add_argument(
        "workspace_dir",
        nargs="?",
        default=".",
        help="Path to the workspace directory (contains repos/ subdirectory)",
    )
    args = parser.parse_args()
    main(args.workspace_dir)
