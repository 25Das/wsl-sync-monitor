"""
Resumo da cobertura de testes por arquivo de src/ (arquivos cobertura.xml do coverlet), do menos ao mais coberto.
Junta vários relatórios linha a linha: uma linha conta como coberta se algum conjunto de testes passou por ela.
Uso: python tools/coverage-summary.py relatorio1.xml [relatorio2.xml ...] > cobertura.md
"""
import sys, collections, xml.etree.ElementTree as ET

lines = collections.defaultdict(dict)  # arquivo -> {número da linha: coberta?}
for path in sys.argv[1:]:
    for c in ET.parse(path).getroot().iter('class'):
        name = c.get('filename').replace('\\', '/').split('/')[-1]
        if name.endswith('Tests.cs') or name == 'Program.cs' and 'Microsoft.NET.Test.Sdk' in c.get('name', ''):
            continue
        for line in c.iter('line'):
            n = int(line.get('number'))
            lines[name][n] = lines[name].get(n, False) or int(line.get('hits')) > 0

rows = [(name, sum(v.values()), len(v)) for name, v in lines.items()]
hit = sum(h for _, h, _ in rows); total = sum(t for _, _, t in rows)
print("# Cobertura dos testes\n")
print(f"**Total: {hit}/{total} linhas ({100 * hit / max(1, total):.0f}%)**\n")
print("| Arquivo | Linhas cobertas | % |\n|---|---|---|")
for name, h, t in sorted(rows, key=lambda r: (r[1] / max(1, r[2]), r[0])):
    print(f"| {name} | {h}/{t} | {100 * h / max(1, t):.0f}% |")
