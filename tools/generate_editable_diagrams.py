#!/usr/bin/env python3
"""
Generates EDITABLE versions of the project diagrams:
  * draw.io (.drawio, mxGraph XML) — 5 diagrams as separate files + one multi-page file
      - ER diagrams: real draw.io tables (one row per column), ER connectors (crow's foot),
        module containers in the module colours; layout taken from Graphviz.
      - data flow and architecture: shapes, containers and labelled connectors.
  * DBML (dbdiagram.io) — the logical schema of the OLTP core, with table groups,
    colours, enums, notes, indexes and all relationships.

Usage: python3 tools/generate_editable_diagrams.py   (after tools/generate_db_docs.py, which writes the .dot files)
       Connects like tools/generate_db_docs.py (PG* variables, otherwise .env).
Needs: psql, graphviz (dot). Re-run after schema changes.
"""
import html
import json
import os
import re
import subprocess
import sys
from datetime import date
from xml.sax.saxutils import escape as xml_escape

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from generate_db_docs import (CATALOG_SQL, COLOURS, MODULE_OF, ROOT, TECHNICAL_TABLES,  # noqa: E402
                              key_flags, psql_json, short_type)

DB_DIAG = os.path.join(ROOT, "docs", "database", "diagrams")
ARCH_DIAG = os.path.join(ROOT, "docs", "architecture", "diagrams")
CORE = ["ref", "iam", "catalog", "inventory", "fleet", "procurement", "forecast", "audit"]
SCALE = 1.0   # Graphviz points -> draw.io px


# --------------------------------------------------------------------------- helpers
def attr(value: str) -> str:
    """Escape a value for an XML attribute (draw.io stores HTML labels inside attributes)."""
    return xml_escape(value, {'"': "&quot;", "\n": "&#10;"})


class Page:
    def __init__(self, name: str):
        self.name = name
        self.cells = []
        self._id = 1

    def nid(self) -> str:
        self._id += 1
        return f"c{self._id}"

    def vertex(self, value, style, x, y, w, h, parent="1", cid=None):
        cid = cid or self.nid()
        self.cells.append(
            f'<mxCell id="{cid}" value="{attr(value)}" style="{attr(style)}" vertex="1" parent="{parent}">'
            f'<mxGeometry x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" as="geometry"/></mxCell>')
        return cid

    def edge(self, src, dst, style, value=""):
        cid = self.nid()
        self.cells.append(
            f'<mxCell id="{cid}" value="{attr(value)}" style="{attr(style)}" edge="1" parent="1" source="{src}" target="{dst}">'
            f'<mxGeometry relative="1" as="geometry"/></mxCell>')
        return cid

    def xml(self) -> str:
        body = "".join(self.cells)
        return (f'<diagram id="{re.sub(r"[^a-z0-9]+", "-", self.name.lower())}" name="{attr(self.name)}">'
                f'<mxGraphModel dx="1600" dy="1000" grid="1" gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" '
                f'fold="1" page="0" pageScale="1" pageWidth="1654" pageHeight="1169" math="0" shadow="0">'
                f'<root><mxCell id="0"/><mxCell id="1" parent="0"/>{body}</root></mxGraphModel></diagram>')


def write_drawio(path, pages):
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(f'<mxfile host="SmartSpare generator" modified="{date.today().isoformat()}" version="24.0.0" type="device">')
        fh.write("".join(p.xml() for p in pages))
        fh.write("</mxfile>\n")


def graphviz_layout(dot_path):
    """Returns (height, nodes, clusters, edges) in Graphviz points with a top-left origin."""
    # json0 = layout without xdot drawing operations: only positions and sizes are needed, and Graphviz 2.42
    # writes garbage bytes into the drawing operations of rounded HTML table cells.
    data = json.loads(subprocess.run(["dot", "-Tjson0", dot_path], capture_output=True, check=True).stdout.decode("utf-8"))
    H = float(data["bb"].split(",")[3])
    nodes, clusters = {}, []
    for o in data.get("objects", []):
        if "nodes" in o:                        # subgraph / cluster
            if "bb" in o:
                x0, y0, x1, y1 = map(float, o["bb"].split(","))
                clusters.append({"name": o["name"], "x": x0, "y": H - y1, "w": x1 - x0, "h": y1 - y0,
                                 "label": o.get("label", ""), "fill": o.get("fillcolor") or o.get("bgcolor"),
                                 "color": o.get("color")})
            continue
        if "pos" not in o:
            continue
        x, y = map(float, o["pos"].split(","))
        w, h = float(o["width"]) * 72, float(o["height"]) * 72
        nodes[o["_gvid"]] = {**o, "x": x - w / 2, "y": H - y - h / 2, "w": w, "h": h}
    edges = [{"tail": e["tail"], "head": e["head"], "label": e.get("label", ""),
              "style": e.get("style", ""), "arrowtail": e.get("arrowtail"), "arrowhead": e.get("arrowhead")}
             for e in data.get("edges", [])]
    return H, nodes, clusters, edges


def plain_label(text):
    """Graphviz label -> draw.io HTML label (HTML labels are kept, plain ones escaped, \\n -> <br>)."""
    text = text or ""
    if re.match(r"^<[A-Za-z]", text):           # Graphviz HTML-like label, e.g. <B>title</B>
        return text
    return "<br>".join(html.escape(line, quote=False) for line in text.replace("\\n", "\n").split("\n"))


# --------------------------------------------------------------------------- ER diagrams
ER_ARROW = {  # graphviz arrow name -> draw.io ER arrow
    "crowtee": "ERmandMany", "crowodot": "ERzeroToMany", "teetee": "ERmandOne", "teeodot": "ERzeroToOne",
}


def er_page(tables, schemas, dot_file, title, compact):
    _, nodes, clusters, edges = graphviz_layout(os.path.join(DB_DIAG, dot_file))
    by_name = {f"{t['schema']}.{t['name']}": t for t in tables}
    page = Page(title)
    page.vertex(f"<b>{html.escape(title)}</b>", "text;html=1;fontSize=22;align=left;verticalAlign=top;", 10, -60, 1200, 40)
    for c in clusters:
        s = c["name"].replace("cluster_", "")
        fill, border = COLOURS.get(s, ("#FFFFFF", "#888888"))
        page.vertex(f"<b>{s}</b> — {html.escape(MODULE_OF.get(s, ''))}",
                    f"rounded=1;arcSize=2;whiteSpace=wrap;html=1;fillColor={fill};strokeColor={border};strokeWidth=2;"
                    "verticalAlign=top;align=center;fontSize=16;spacingTop=4;container=0;",
                    c["x"] * SCALE, c["y"] * SCALE, c["w"] * SCALE, c["h"] * SCALE)
    ids = {}
    for n in nodes.values():
        name = n["name"]
        if name == "legend":
            continue
        t = by_name[name]
        s = t["schema"]
        _, border = COLOURS[s]
        pk, fk, uq = key_flags(t)
        cols = t["columns"]
        shown = [c for c in cols if (not compact) or c["name"] in pk or c["name"] in fk or c["name"] in uq]
        hidden = len(cols) - len(shown)
        rows = len(shown) + (1 if hidden else 0)
        header = 24
        row_h = max(16.0, (n["h"] * SCALE - header) / max(rows, 1))
        width = n["w"] * SCALE
        tid = page.vertex(t["name"],
                          f"swimlane;fontStyle=1;childLayout=stackLayout;horizontal=1;startSize={header};horizontalStack=0;"
                          f"resizeParent=1;resizeParentMax=0;resizeLast=0;collapsible=1;marginBottom=0;html=1;"
                          f"fillColor={border};fontColor=#FFFFFF;strokeColor={border};swimlaneFillColor=#FFFFFF;fontSize=12;",
                          n["x"] * SCALE, n["y"] * SCALE, width, header + row_h * rows)
        ids[name] = tid
        for i, c in enumerate(shown):
            mark = " ".join(k for k, ok in (("PK", c["name"] in pk), ("FK", c["name"] in fk),
                                            ("UQ", c["name"] in uq and c["name"] not in pk)) if ok)
            nm = f"<b>{html.escape(c['name'])}</b>" if c["name"] in pk else html.escape(c["name"])
            if c["not_null"] and c["name"] not in pk:
                nm += "*"
            value = (f"<font color=\"#555555\">{mark}</font> " if mark else "") + nm + \
                    f" <font color=\"#888888\">{html.escape(short_type(c['type']))}</font>"
            page.vertex(value, "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;"
                               "spacingLeft=6;overflow=hidden;rotatable=0;points=[[0,0.5],[1,0.5]];"
                               "portConstraint=eastwest;fontSize=10;whiteSpace=nowrap;",
                        0, header + i * row_h, width, row_h, parent=tid)
        if hidden:
            page.vertex(f"<i>+ {hidden} attributes (see data dictionary)</i>",
                        "text;html=1;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;spacingLeft=6;"
                        "fontSize=10;fontColor=#888888;", 0, header + len(shown) * row_h, width, row_h, parent=tid)
    for e in edges:
        a, b = nodes.get(e["tail"]), nodes.get(e["head"])
        if not a or not b or a["name"] not in ids or b["name"] not in ids:
            continue
        page.edge(ids[a["name"]], ids[b["name"]],
                  "edgeStyle=entityRelationEdgeStyle;html=1;fontSize=10;strokeColor=#555555;"
                  f"startArrow={ER_ARROW.get(e['arrowtail'], 'ERmany')};startFill=0;"
                  f"endArrow={ER_ARROW.get(e['arrowhead'], 'ERmandOne')};endFill=0;")
    legend = next((n for n in nodes.values() if n["name"] == "legend"), None)
    if legend:
        page.vertex("<b>Legend</b><br>PK primary key · FK foreign key · UQ unique · * NOT NULL<br>"
                    "Crow's foot = many · bar = exactly one · circle = optional<br>"
                    "Colour = module / schema<br>FKs to iam.app_user (created_by, approved_by …) omitted for clarity",
                    "rounded=0;whiteSpace=wrap;html=1;align=left;spacingLeft=8;fontSize=12;fillColor=#FFFFFF;strokeColor=#333333;",
                    legend["x"], legend["y"], max(legend["w"], 380), max(legend["h"], 100))
    return page


# --------------------------------------------------------------------------- generic diagrams
SHAPE_STYLE = {
    "box": "rounded=1;whiteSpace=wrap;html=1;arcSize=12;",
    "cylinder": "shape=cylinder3;whiteSpace=wrap;html=1;boundedLbl=1;backgroundOutline=1;size=12;",
    "diamond": "rhombus;whiteSpace=wrap;html=1;",
    # Text-only nodes: cluster titles (plaintext) and HTML-table nodes (plain) draw no box of their own.
    "plaintext": "text;whiteSpace=wrap;html=1;align=center;verticalAlign=middle;strokeColor=none;fillColor=none;",
    "plain": "text;whiteSpace=wrap;html=1;align=center;verticalAlign=middle;strokeColor=none;fillColor=none;",
}


def generic_page(dot_path, title):
    H, nodes, clusters, edges = graphviz_layout(dot_path)
    page = Page(title)
    page.vertex(f"<b>{html.escape(title)}</b>", "text;html=1;fontSize=20;align=left;verticalAlign=top;", 10, -50, 1200, 36)
    for c in sorted(clusters, key=lambda c: -c["w"] * c["h"]):     # big containers first (background)
        dashed = "dashed=1;" if "dashed" in (c.get("label") or "") else ""
        page.vertex(plain_label(c["label"]),
                    f"rounded=1;arcSize=3;whiteSpace=wrap;html=1;verticalAlign=top;fontSize=14;fontStyle=1;{dashed}"
                    f"fillColor={c['fill'] or '#FFFFFF'};strokeColor={c['color'] or '#888888'};strokeWidth=2;",
                    c["x"], c["y"], c["w"], c["h"])
    ids = {}
    for n in nodes.values():
        kind = n.get("shape", "box")
        shape = SHAPE_STYLE.get(kind, SHAPE_STYLE["box"])
        if kind in ("plaintext", "plain"):
            colours = ""                                # the shape style already removes box and fill
        else:
            colours = f"fillColor={n.get('fillcolor', '#FFFFFF')};strokeColor={n.get('color', '#555555')};"
        dashed = "dashed=1;" if "dashed" in (n.get("style") or "") else ""   # planned components
        ids[n["_gvid"]] = page.vertex(plain_label(n.get("label", n["name"])),
                                      f"{shape}{colours}{dashed}fontSize=10;",
                                      n["x"], n["y"], n["w"], n["h"])
    for e in edges:
        if e["style"] == "invis" or e["tail"] not in ids or e["head"] not in ids:
            continue
        style = "edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;fontSize=10;strokeColor=#555555;endArrow=block;endFill=1;"
        if e["style"] == "dashed":
            style += "dashed=1;"
        if e["style"] == "dotted":
            style += "dashed=1;dashPattern=1 3;endArrow=none;"
        if e.get("arrowhead") == "none":
            style += "endArrow=none;"
        page.edge(ids[e["tail"]], ids[e["head"]], style, plain_label(e["label"]).strip())
    return page


# --------------------------------------------------------------------------- DBML
def dbml_str(s):
    return "'" + (s or "").replace("\\", "\\\\").replace("'", "\\'").replace("\n", " ") + "'"


def write_dbml(tables, enums, path):
    core = [t for t in tables if t["schema"] in CORE and t["kind"] in "rp" and t["name"] not in TECHNICAL_TABLES]
    names = {f"{t['schema']}.{t['name']}" for t in core}
    out = ["// SmartSpare — logical schema of the OLTP core (PostgreSQL 17)",
           "// GENERATED by tools/generate_editable_diagrams.py from the database catalogue. Do not edit by hand:",
           "// the source of truth are the EF Core migrations of each module and their SQL parts in db/sql/.",
           "// References between modules are plain ids, not foreign keys (ADR 0011).",
           "// Open at https://dbdiagram.io (Import -> DBML / paste).",
           "",
           "Project SmartSpare {",
           "  database_type: 'PostgreSQL'",
           "  Note: '''Agricultural machinery maintenance planning and spare-parts demand forecasting.",
           "  One schema per module of the modular monolith. Business rules (CHECK, triggers, functions),",
           "  partitions, views and roles are documented in docs/database/DATABASE_DESIGN.md.'''",
           "}", ""]
    for e in enums:
        if e["schema"] not in CORE:
            continue
        out.append(f"Enum {e['schema']}.{e['name']} {{")
        out += [f'  "{v}"' for v in e["values"]]
        out.append("}")
        out.append("")
    refs = []
    enum_names = {f"{e['schema']}.{e['name']}" for e in enums if e["schema"] in CORE}
    for t in core:
        pk, fk, uq = key_flags(t)
        _, border = COLOURS[t["schema"]]
        note = f" [headercolor: {border}, note: {dbml_str(t.get('comment'))}]" if t.get("comment") else f" [headercolor: {border}]"
        out.append(f"Table {t['schema']}.{t['name']}{note} {{")
        for c in t["columns"]:
            settings = []
            if c["name"] in pk and len(pk) == 1:
                settings.append("pk")
            if c["identity"]:
                settings.append("increment")
            if c["not_null"] and not (c["name"] in pk and len(pk) == 1):
                settings.append("not null")
            if c["name"] in uq:
                settings.append("unique")
            if c["default"] and not c["identity"]:
                settings.append(f"default: `{c['default'].replace('`', '')}`")
            if c.get("comment"):
                settings.append(f"note: {dbml_str(c['comment'])}")
            typ = c["type"]
            typ_sql = typ if typ in enum_names else f'"{typ}"'      # enum types link to the Enum definitions
            out.append(f'  "{c["name"]}" {typ_sql}' + (f" [{', '.join(settings)}]" if settings else ""))
        idx = []
        if len(pk) > 1:
            pk_cols = next(k["cols"] for k in t["constraints"] if k["type"] == "p")
            idx.append(f"    ({', '.join(pk_cols)}) [pk]")
        for k in t.get("constraints") or []:
            if k["type"] == "u" and len(k["cols"] or []) > 1:
                idx.append(f"    ({', '.join(k['cols'])}) [unique, name: '{k['name']}']")
        if idx:
            out.append("")
            out.append("  indexes {")
            out += idx
            out.append("  }")
        checks = [k for k in (t.get("constraints") or []) if k["type"] in ("c", "x")]
        if checks:
            out.append("")
            out.append("  Note: " + dbml_str("Integrity rules: " + "; ".join(f"{k['name']}: {k['def']}" for k in checks)))
        out.append("}")
        out.append("")
        for k in t.get("constraints") or []:
            if k["type"] != "f" or k["ref"] not in names:
                continue
            ref_t = next(x for x in core if f"{x['schema']}.{x['name']}" == k["ref"])
            ref_cols = next(kk["cols"] for kk in ref_t["constraints"] if kk["type"] in ("p",)) \
                if len(k["cols"]) > 1 or True else None
            # referenced columns: resolve from the constraint definition
            m = re.search(r"REFERENCES\s+\S+\(([^)]*)\)", k["def"])
            ref_cols = [c.strip() for c in m.group(1).split(",")] if m else ref_cols
            on_delete = re.search(r"ON DELETE (CASCADE|SET NULL|RESTRICT|NO ACTION)", k["def"])
            child = f"{t['schema']}.{t['name']}"
            if len(k["cols"]) == 1:
                lhs, rhs = f"{child}.{k['cols'][0]}", f"{k['ref']}.{ref_cols[0]}"
            else:
                lhs, rhs = f"{child}.({', '.join(k['cols'])})", f"{k['ref']}.({', '.join(ref_cols)})"
            one_to_one = set(k["cols"]) == set(pk) and len(pk) == len(k["cols"])
            op = "-" if one_to_one else ">"
            setting = f" [delete: {on_delete.group(1).lower()}]" if on_delete else ""
            refs.append(f"Ref {k['name']}: {lhs} {op} {rhs}{setting}")
    out.append("// ---------------------------------------------------------------- relationships")
    out += refs
    out.append("")
    out.append("// ---------------------------------------------------------------- modules")
    for s in CORE:
        members = [f"{t['schema']}.{t['name']}" for t in core if t["schema"] == s]
        if members:
            _, border = COLOURS[s]
            out.append(f"TableGroup {s} [color: {border}, note: {dbml_str(MODULE_OF[s])}] {{")
            out += [f"  {m}" for m in members]
            out.append("}")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out) + "\n")
    return len(core), len(refs)


ENUM_SQL = """
SELECT json_agg(json_build_object('schema', n.nspname, 'name', t.typname,
       'values', (SELECT json_agg(e.enumlabel ORDER BY e.enumsortorder) FROM pg_enum e WHERE e.enumtypid = t.oid))
       ORDER BY n.nspname, t.typname)
FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE t.typtype = 'e' AND n.nspname = ANY(%(schemas)s);
"""


def main():
    tables = psql_json(CATALOG_SQL)
    enums = psql_json(ENUM_SQL) or []
    pages = [
        er_page(tables, CORE, "logical_schema_overview.dot", "Logical schema — overview (keys only)", True),
        er_page(tables, CORE, "logical_schema_core.dot", "Logical schema — OLTP core (all attributes)", False),
        er_page(tables, ["raw", "staging", "inventory", "catalog", "ref"], "logical_schema_import_layers.dot",
                "Import data layers raw → staging → core", True),
        generic_page(os.path.join(DB_DIAG, "data_flow.dot"), "Data flow through the database layers"),
        generic_page(os.path.join(ARCH_DIAG, "architecture.dot"), "Solution architecture"),
    ]
    files = ["logical_schema_overview", "logical_schema_core", "logical_schema_import_layers", "data_flow", "architecture"]
    for page, f in zip(pages, files):
        target = ARCH_DIAG if f == "architecture" else DB_DIAG
        write_drawio(os.path.join(target, f + ".drawio"), [page])
    write_drawio(os.path.join(ROOT, "docs", "SmartSpare_diagrams.drawio"), pages)
    n_tables, n_refs = write_dbml(tables, enums, os.path.join(DB_DIAG, "smartspare_core.dbml"))
    print(f"draw.io: {len(pages)} diagrams; DBML: {n_tables} tables, {n_refs} relationships")


if __name__ == "__main__":
    sys.exit(main())
