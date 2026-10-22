"""
Resumo da cobertura de testes por arquivo de src/ (relatórios no formato cobertura: coverlet ou o coletor da
Microsoft), do menos ao mais coberto.
Junta vários relatórios linha a linha: uma linha conta como coberta se algum conjunto de testes passou por ela.
Uso: python tools/coverage-summary.py relatorio1.xml [relatorio2.xml ...] > cobertura.md
"""
import sys, collections, xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding='utf-8')  # no Windows a saída padrão não é UTF-8

lines = collections.defaultdict(dict)  # arquivo -> {número da linha: coberta?}
per_report = []
for path in sys.argv[1:]:
    covered = total_lines = 0
    root = ET.parse(path).getroot()
    # o nome do arquivo pode ser relativo a um <source> (o coverlet usa a pasta comum, às vezes a própria src/)
    sources = [s.text or '' for s in root.iter('source')] or ['']
    for c in root.iter('class'):
        filename = c.get('filename')
        if len(sources) == 1 and not (filename.startswith('/') or filename[1:3] == ':\\'):
            filename = sources[0].rstrip('/\\') + '/' + filename
        path_ = filename.replace('\\', '/')
        if '/src/' not in '/' + path_ and not path_.startswith('src/'):
            continue  # só o código do app (fora: testes, bibliotecas, arquivos gerados)
        name = path_.split('/src/')[-1].removeprefix('src/')
        for line in c.iter('line'):
            n = int(line.get('number'))
            lines[name][n] = lines[name].get(n, False) or int(line.get('hits')) > 0
            total_lines += 1; covered += int(line.get('hits')) > 0
    per_report.append((path, covered, total_lines, len(root.findall('.//class')), sources))

rows = [(name, sum(v.values()), len(v)) for name, v in lines.items()]
hit = sum(h for _, h, _ in rows); total = sum(t for _, _, t in rows)
print("# Cobertura dos testes\n")
print(f"**Total: {hit}/{total} linhas ({100 * hit / max(1, total):.0f}%)**\n")
print("| Arquivo | Linhas cobertas | % |\n|---|---|---|")
for name, h, t in sorted(rows, key=lambda r: (r[1] / max(1, r[2]), r[0])):
    print(f"| {name} | {h}/{t} | {100 * h / max(1, t):.0f}% |")
print("\nPor relatório (linhas de src/ cobertas):\n")
for path, h, t, classes, sources in per_report:
    print(f"- {path.replace(chr(92), '/').split('/')[-1]}: {h}/{t} ({classes} classes; origem: {', '.join(sources)})")
