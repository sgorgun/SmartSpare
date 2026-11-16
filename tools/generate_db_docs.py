#!/usr/bin/env python3
"""
Generates the data dictionary (Markdown) and the colour-coded logical schema
(Graphviz -> SVG/PNG) directly from the PostgreSQL system catalogue, so the
documentation can never drift from the migrations.

Usage:  PGHOST=... PGDATABASE=smartspare PGUSER=... python3 tools/generate_db_docs.py
Needs:  psql and graphviz (dot) on PATH. No Python packages required.
"""
import json
import os
import subprocess
import sys
from collections import defaultdict
from datetime import date

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DOCS = os.path.join(ROOT, "docs", "database")
OUT_DIAG = os.path.join(OUT_DOCS, "diagrams")

SCHEMAS = ["ref", "iam", "catalog", "inventory", "fleet", "procurement", "forecast",
           "audit", "raw", "staging", "mart", "util", "meta"]
MODULE_OF = {
    "ref": "Reference data", "iam": "Identity & Access", "catalog": "Inventory — catalogue",
    "inventory": "Inventory — stock & transfers", "fleet": "Fleet & Maintenance",
    "procurement": "Procurement", "forecast": "Forecasting (ML)", "audit": "Audit",
    "raw": "Data layer 1 — raw", "staging": "Data layer 2 — staging", "mart": "Data layer 4 — data mart",
    "util": "Technical utilities", "meta": "Schema versioning",
}
# colour-blind-safe pastel palette (fill, border) per schema
COLOURS = {
    "ref": ("#EEEEEE", "#777777"), "iam": ("#F3E5F5", "#8E24AA"), "catalog": ("#E3F2FD", "#1E88E5"),
    "inventory": ("#E0F2F1", "#00897B"), "fleet": ("#FFF3E0", "#FB8C00"), "procurement": ("#FCE4EC", "#D81B60"),
    "forecast": ("#E8EAF6", "#3949AB"), "audit": ("#ECEFF1", "#546E7A"), "raw": ("#FFFDE7", "#C0A000"),
    "staging": ("#F9FBE7", "#9E9D24"), "mart": ("#E8F5E9", "#43A047"), "util": ("#FAFAFA", "#9E9E9E"),
    "meta": ("#FAFAFA", "#9E9E9E"),
}

CATALOG_SQL = r"""
WITH t AS (
  SELECT c.oid, n.nspname AS schema, c.relname AS name, c.relkind AS kind,
         obj_description(c.oid, 'pg_class') AS comment,
         CASE WHEN c.relkind = 'p'
              THEN (SELECT sum(ch.reltuples)::bigint FROM pg_inherits i JOIN pg_class ch ON ch.oid = i.inhrelid WHERE i.inhparent = c.oid)
              ELSE greatest(c.reltuples, 0)::bigint END AS est_rows,
         (SELECT count(*) FROM pg_inherits i WHERE i.inhparent = c.oid) AS partitions,
         CASE WHEN c.relkind = 'p' THEN pg_get_partkeydef(c.oid) END AS partition_key
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname = ANY(%(schemas)s) AND c.relkind IN ('r','p','v','m') AND NOT c.relispartition
)
SELECT json_agg(json_build_object(
  'schema', t.schema, 'name', t.name, 'kind', t.kind, 'comment', t.comment,
  'rows', t.est_rows, 'partitions', t.partitions, 'partition_key', t.partition_key,
  'columns', (SELECT json_agg(json_build_object(
        'name', a.attname, 'type', format_type(a.atttypid, a.atttypmod), 'not_null', a.attnotnull,
        'default', pg_get_expr(d.adbin, d.adrelid), 'generated', a.attgenerated <> '',
        'identity', a.attidentity <> '', 'comment', col_description(t.oid, a.attnum)) ORDER BY a.attnum)
      FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
      WHERE a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped),
  'constraints', (SELECT json_agg(json_build_object(
        'name', k.conname, 'type', k.contype, 'def', pg_get_constraintdef(k.oid),
        'cols', (SELECT json_agg(a.attname ORDER BY x.ord) FROM unnest(k.conkey) WITH ORDINALITY x(n, ord)
                 JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = x.n),
        'ref', CASE WHEN k.contype = 'f' THEN k.confrelid::regclass::text END) ORDER BY k.contype, k.conname)
      FROM pg_constraint k WHERE k.conrelid = t.oid),
  'indexes', (SELECT json_agg(json_build_object('name', ic.relname, 'def', pg_get_indexdef(ic.oid)) ORDER BY ic.relname)
      FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid WHERE i.indrelid = t.oid),
  'triggers', (SELECT json_agg(tg.tgname || ': ' || tg.tgfoid::regproc::text ORDER BY tg.tgname)
      FROM pg_trigger tg WHERE tg.tgrelid = t.oid AND NOT tg.tgisinternal)
) ORDER BY array_position(%(schemas)s, t.schema::text), t.name)
FROM t;
"""

ROUTINES_SQL = r"""
SELECT json_agg(json_build_object('schema', n.nspname, 'name', p.proname,
         'signature', p.oid::regprocedure::text, 'kind', p.prokind, 'secdef', p.prosecdef,
         'returns', CASE WHEN p.prokind = 'p' THEN 'procedure' ELSE pg_get_function_result(p.oid) END,
         'language', l.lanname, 'comment', obj_description(p.oid, 'pg_proc'),
         'is_trigger', p.prorettype = 'trigger'::regtype)
       ORDER BY array_position(%(schemas)s, n.nspname::text), p.proname)
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
WHERE n.nspname = ANY(%(schemas)s);
"""


def psql_json(sql: str):
    arr = "ARRAY[" + ",".join(f"'{s}'" for s in SCHEMAS) + "]::text[]"
    sql = sql.replace("%(schemas)s", arr)
    res = subprocess.run(["psql", "-X", "-q", "-tA", "-c", sql], capture_output=True, text=True, check=True)
    return json.loads(res.stdout)


def md_escape(text):
    return (text or "").replace("|", "\\|").replace("\n", " ")


KIND = {"r": "table", "p": "partitioned table", "v": "view", "m": "materialized view"}


def key_flags(table):
    pk, fk, uq = set(), {}, set()
    for c in table.get("constraints") or []:
        cols = c.get("cols") or []
        if c["type"] == "p":
            pk.update(cols)
        elif c["type"] == "f":
            for col in cols:
                fk[col] = c["ref"]
        elif c["type"] == "u":
            if len(cols) == 1:
                uq.update(cols)
    return pk, fk, uq


def write_dictionary(tables, routines):
    lines = []
    lines.append("# SmartSpare — Data Dictionary\n")
    lines.append(f"_Generated from the PostgreSQL catalogue by `tools/generate_db_docs.py` on {date.today().isoformat()}. "
                 "Do not edit by hand — change the migrations and regenerate._\n")
    lines.append("Row counts are those of the synthetic demo dataset (`db/testdata/T001`, 2,000 SKUs).\n")
    lines.append("**Legend:** PK = primary key, FK = foreign key (→ referenced table), UQ = unique, "
                 "NN = NOT NULL, GEN = generated column, ID = identity.\n")

    by_schema = defaultdict(list)
    for t in tables:
        by_schema[t["schema"]].append(t)

    # summary
    lines.append("## 1. Overview\n")
    lines.append("| Schema | Layer / module | Tables | Views | Mat. views |")
    lines.append("|---|---|---:|---:|---:|")
    for s in SCHEMAS:
        ts = by_schema.get(s, [])
        lines.append(f"| `{s}` | {MODULE_OF[s]} | {sum(1 for t in ts if t['kind'] in 'rp')} | "
                     f"{sum(1 for t in ts if t['kind'] == 'v')} | {sum(1 for t in ts if t['kind'] == 'm')} |")
    lines.append("")

    n = 2
    for s in SCHEMAS:
        ts = by_schema.get(s, [])
        if not ts:
            continue
        lines.append(f"## {n}. Schema `{s}` — {MODULE_OF[s]}\n")
        n += 1
        for t in ts:
            pk, fk, uq = key_flags(t)
            title = f"### `{s}.{t['name']}` ({KIND[t['kind']]})"
            lines.append(title + "\n")
            if t.get("comment"):
                lines.append(t["comment"] + "\n")
            meta = []
            if t["kind"] in "rpm":
                meta.append(f"Rows (demo): **{t['rows']:,}**")
            if t.get("partition_key"):
                meta.append(f"Partitioning: `{t['partition_key']}`, {t['partitions']} partitions")
            if meta:
                lines.append(" · ".join(meta) + "\n")
            lines.append("| Column | Type | Keys / flags | Default | Description |")
            lines.append("|---|---|---|---|---|")
            for c in t.get("columns") or []:
                flags = []
                if c["name"] in pk: flags.append("PK")
                if c["name"] in fk: flags.append(f"FK → `{fk[c['name']]}`")
                if c["name"] in uq: flags.append("UQ")
                if c["not_null"]: flags.append("NN")
                if c["generated"]: flags.append("GEN")
                if c["identity"]: flags.append("ID")
                default = c["default"] or ""
                if c["identity"]:
                    default = "identity"
                lines.append(f"| `{c['name']}` | `{c['type']}` | {', '.join(flags)} | "
                             f"{('`' + md_escape(default) + '`') if default else ''} | {md_escape(c.get('comment'))} |")
            lines.append("")
            cons = [c for c in (t.get("constraints") or []) if c["type"] in ("c", "x", "u", "f", "p")]
            checks = [c for c in cons if c["type"] in ("c", "x")]
            others = [c for c in cons if c["type"] in ("u", "p", "f")]
            if checks or others:
                lines.append("**Data quality expectations / integrity rules**\n")
                for c in others:
                    label = {"p": "Primary key", "u": "Unique", "f": "Foreign key"}[c["type"]]
                    lines.append(f"- {label} `{c['name']}`: `{md_escape(c['def'])}`")
                for c in checks:
                    label = "Exclusion" if c["type"] == "x" else "Check"
                    lines.append(f"- {label} `{c['name']}`: `{md_escape(c['def'])}`")
                lines.append("")
            idx = [i for i in (t.get("indexes") or [])]
            if idx:
                lines.append("**Indexes**\n")
                for i in idx:
                    lines.append(f"- `{i['name']}` — `{md_escape(i['def'].split(' USING ', 1)[-1] if ' USING ' in i['def'] else i['def'])}`")
                lines.append("")
            if t.get("triggers"):
                lines.append("**Triggers:** " + ", ".join(f"`{x}`" for x in t["triggers"]) + "\n")

    lines.append(f"## {n}. Functions, procedures and trigger functions\n")
    lines.append("| Routine | Kind | Security | Returns | Description |")
    lines.append("|---|---|---|---|---|")
    for r in routines or []:
        kind = "procedure" if r["kind"] == "p" else ("trigger function" if r["is_trigger"] else "function")
        sec = "DEFINER" if r["secdef"] else "invoker"
        lines.append(f"| `{md_escape(r['signature'])}` | {kind} ({r['language']}) | {sec} | `{md_escape(r['returns'])}` | {md_escape(r.get('comment'))} |")
    lines.append("")
    with open(os.path.join(OUT_DOCS, "DATA_DICTIONARY.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))


def html(text):
    return (text or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def short_type(t):
    return (t.replace("timestamp with time zone", "timestamptz").replace("character varying", "varchar")
             .replace("character", "char").replace("double precision", "float8"))


def write_er(tables, schemas, filename, title, compact=False, skip_user_edges=True):
    """Logical schema: one cluster per schema (module), crow's-foot FK edges."""
    tbls = [t for t in tables if t["schema"] in schemas and t["kind"] in "rp"]
    names = {f"{t['schema']}.{t['name']}" for t in tbls}
    out = ['digraph ER {', '  graph [rankdir=LR, splines=true, nodesep=0.35, ranksep=1.1, fontname="Helvetica", '
           f'label=<<B>{html(title)}</B>>, labelloc=t, fontsize=22, pad=0.4];',
           '  node [shape=plain, fontname="Helvetica", fontsize=10];',
           '  edge [color="#555555", arrowsize=0.8, penwidth=1.0];']
    for s in schemas:
        ts = [t for t in tbls if t["schema"] == s]
        if not ts:
            continue
        fill, border = COLOURS[s]
        out.append(f'  subgraph cluster_{s} {{ label=<<B>{html(s)}</B> — {html(MODULE_OF[s])}>; style="rounded,filled"; '
                   f'fillcolor="{fill}"; color="{border}"; fontsize=14; penwidth=2;')
        for t in ts:
            pk, fk, uq = key_flags(t)
            rows = [f'<TR><TD BGCOLOR="{border}" COLSPAN="2"><FONT COLOR="white"><B>{html(t["name"])}</B></FONT></TD></TR>']
            cols = t.get("columns") or []
            shown = [c for c in cols if (not compact) or c["name"] in pk or c["name"] in fk or c["name"] in uq]
            for c in shown:
                mark = ("PK " if c["name"] in pk else "") + ("FK " if c["name"] in fk else "") + ("UQ" if c["name"] in uq and c["name"] not in pk else "")
                name = f"<B>{html(c['name'])}</B>" if c["name"] in pk else html(c["name"])
                if c["not_null"] and c["name"] not in pk:
                    name += "*"
                rows.append(f'<TR><TD ALIGN="LEFT" PORT="{html(c["name"])}">{mark}{name}</TD>'
                            f'<TD ALIGN="LEFT"><FONT COLOR="#666666">{html(short_type(c["type"]))}</FONT></TD></TR>')
            if compact and len(shown) < len(cols):
                rows.append(f'<TR><TD ALIGN="LEFT" COLSPAN="2"><I><FONT COLOR="#888888">+ {len(cols) - len(shown)} attributes</FONT></I></TD></TR>')
            out.append(f'    "{s}.{t["name"]}" [label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3" BGCOLOR="white" COLOR="{border}">{"".join(rows)}</TABLE>>];')
        out.append("  }")
    for t in tbls:
        for c in t.get("constraints") or []:
            if c["type"] != "f" or c["ref"] not in names:
                continue
            if skip_user_edges and c["ref"] == "iam.app_user" and t["name"] not in ("user_role", "user_brand"):
                continue   # "who" columns (created_by, approved_by ...) — listed in the data dictionary
            nullable = any(not col["not_null"] for col in t["columns"] if col["name"] in (c["cols"] or []))
            child = f'{t["schema"]}.{t["name"]}'
            # child (many) --> parent (one); crow's foot at the child end
            out.append(f'  "{child}" -> "{c["ref"]}" [dir=both, arrowtail="{"crowodot" if nullable else "crowtee"}", '
                       f'arrowhead="{"teeodot" if nullable else "teetee"}", tooltip="{html(c["name"])}"];')
    out.append('  legend [shape=plain, label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="2" CELLPADDING="4" BGCOLOR="white">'
               '<TR><TD ALIGN="LEFT"><B>Legend</B></TD></TR>'
               '<TR><TD ALIGN="LEFT">PK primary key · FK foreign key · UQ unique · * NOT NULL</TD></TR>'
               '<TR><TD ALIGN="LEFT">crow\'s foot = many · bar = exactly one · circle = optional</TD></TR>'
               '<TR><TD ALIGN="LEFT">Colour = module / schema (one schema per module)</TD></TR>'
               '<TR><TD ALIGN="LEFT">FKs to iam.app_user (created_by, approved_by ...) omitted for clarity</TD></TR>'
               '</TABLE>>];')
    out.append("}")
    dot = os.path.join(OUT_DIAG, filename + ".dot")
    with open(dot, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out))
    for fmt in ("svg", "png"):
        subprocess.run(["dot", f"-T{fmt}", "-Gdpi=110", dot, "-o", os.path.join(OUT_DIAG, f"{filename}.{fmt}")], check=True)


def main():
    os.makedirs(OUT_DIAG, exist_ok=True)
    tables = psql_json(CATALOG_SQL)
    routines = psql_json(ROUTINES_SQL)
    write_dictionary(tables, routines)
    core = ["ref", "iam", "catalog", "inventory", "fleet", "procurement", "forecast", "audit"]
    write_er(tables, core, "logical_schema_core", "SmartSpare — logical schema of the OLTP core (3NF)", compact=False)
    write_er(tables, core, "logical_schema_overview", "SmartSpare — logical schema overview (keys only)", compact=True)
    write_er(tables, ["raw", "staging", "inventory", "catalog", "ref"], "logical_schema_import_layers",
             "SmartSpare — import data layers raw -> staging -> core", compact=True)
    print(f"{len(tables)} relations, {len(routines)} routines documented")


if __name__ == "__main__":
    sys.exit(main())
