# Источник web-design-guidelines

Этот README добавлен для текущего репозитория. `SKILL.md` — неизменённая копия
исходного файла Vercel; в указанной ниже версии каталог skill содержит только
этот файл, без вспомогательных файлов и символических ссылок.

- Репозиторий: <https://github.com/vercel-labs/agent-skills>
- Исходный каталог: `skills/web-design-guidelines/`
- Commit SHA: `063bee94c3f4df8453406c830b0a7df0f2860278`
- Зафиксированный источник:
  <https://github.com/vercel-labs/agent-skills/tree/063bee94c3f4df8453406c830b0a7df0f2860278/skills/web-design-guidelines>
- SHA-256 `SKILL.md`:
  `f4647ca866a3accf763777f83e7682954f0187cd6bea7eea0399796652414e8f`
- Git blob SHA `SKILL.md`: `ceae92ab319216a68274168fba9b63b998b65997`
- Дата добавления и проверки сети: 2026-10-10.

## Актуальные правила

Skill загружает правила перед каждой проверкой:
<https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md>.
При добавлении адрес вернул HTTP 200 и Markdown размером 8055 байт.
Это подтверждает доступность на дату проверки; содержимое ветки `main`
может измениться. Копия этих правил не подменяет актуальную загрузку.

Для загрузки правил нужен доступ к `raw.githubusercontent.com` по HTTPS.
Для получения исходников и обновления также нужен `github.com`; для проверки
commit SHA и списка файлов через GitHub API — `api.github.com`.

## Воспроизводимое получение и обновление

Получите исходники во временный каталог с проверкой TLS:

```sh
git clone https://github.com/vercel-labs/agent-skills.git /tmp/vercel-agent-skills
git -C /tmp/vercel-agent-skills checkout --detach 063bee94c3f4df8453406c830b0a7df0f2860278
git -C /tmp/vercel-agent-skills ls-tree -r HEAD -- skills/web-design-guidelines/
```

Убедитесь, что строки дерева имеют тип `blob` и режим `100644` или `100755`:
ссылки (`120000`) и подмодули (`160000`) не должны попадать в локальную копию.
Скопируйте **все** исходные файлы каталога как обычные файлы с сохранением
структуры и инструкций. Для зафиксированной версии достаточно:

```sh
cp /tmp/vercel-agent-skills/skills/web-design-guidelines/SKILL.md .agents/skills/web-design-guidelines/SKILL.md
sha256sum .agents/skills/web-design-guidelines/SKILL.md
git diff --check
```

Сравните хеш с приведённым выше. Проверьте YAML front matter `SKILL.md`
(поля `name`, `description`, `metadata`) и существование всех вспомогательных
файлов, на которые он ссылается.

При обновлении выберите новый полный commit SHA вместо плавающей ветки,
просмотрите изменения всего исходного каталога и скопируйте его полное
содержимое. Удаляйте только файлы, подтверждённо удалённые в upstream;
сохраняйте локальную документацию. Обновите SHA, хеши и дату в этом README,
повторите проверку метаданных и доступности актуальных правил:

```sh
curl --fail --show-error --location --max-time 30 \
  https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md \
  --output /tmp/web-interface-guidelines-command.md
```

Если upstream добавит собственный `README.md`, сохраните его без изменений,
а этот локальный документ перенесите в `PROVENANCE.md` и обновите ссылки.
