# Gemini Unity Bridge

Двусторонний мост связи между Gemini CLI и редактором Unity. Этот плагин превращает Unity в интерактивную среду, которой ИИ может управлять программно: анализировать сцену, манипулировать объектами, писать код и диагностировать ошибки в реальном времени.

## 🚀 Как запустить

1. **Откройте проект в Unity**: Плагин инициализируется автоматически при загрузке проекта благодаря атрибуту `[InitializeOnLoad]`.
2. **Проверьте статус**: При старте в консоли Unity появится лог: `[GeminiBridge] Server started on port 12121`.
3. **Ручной перезапуск**: Если нужно перезапустить сервер, используйте меню в Unity: `Gemini > Bridge > Restart Server`.

## 🛠 Как использовать (для пользователя)

Вы можете отправлять запросы к серверу через любой HTTP-клиент (браузер, curl, PowerShell) или использовать встроенные функции Gemini CLI.

### Примеры команд:

- **Проверка связи и ошибок**:
  ```bash
  curl "http://localhost:12121/ping"
  curl "http://localhost:12121/check_errors"
  ```
- **Создать источник света и настроить его**:
  ```bash
  # Создаем объект Light
  curl "http://localhost:12121/create?type=light"
  # Настраиваем интенсивность (допустим ID=100)
  curl "http://localhost:12121/component?id=100&action=set&type=Light&name=intensity&value=2.5"
  ```
- **Управление трансформом**:
  ```bash
  # Повернуть объект к точке
  curl "http://localhost:12121/transform?id=123&action=look_at&target=0,5,0"
  # Сбросить позицию
  curl "http://localhost:12121/transform?id=123&action=reset"
  ```

## 🤖 Возможности для ИИ (Gemini)

1. **Визуальная диагностика**: Захват скриншотов (`/screenshot_base64`) для анализа визуальных багов или верстки UI.
2. **Пространственный анализ**: Выполнение Raycast (`/physics/raycast`) для понимания геометрии сцены.
3. **Генерация кода**: Чтение и запись скриптов (`/filesystem`) с автоматическим импортом в Unity.
4. **Манипуляция Inspector**: Доступ ко всем полям компонентов через `SerializedObject`, включая скрытые и приватные.
5. **Пакетные операции**: Выполнение цепочки действий за один HTTP-запрос через `/batch`.

## 📝 Список API эндпоинтов (Порт 12121)

| Эндпоинт | Параметры | Описание |
| :--- | :--- | :--- |
| `/ping` | Нет | Состояние сервера, версия и имя проекта. |
| `/check_errors` | Нет | Возвращает `true`, если в проекте есть ошибки компиляции. |
| `/hierarchy` | `full` | Дерево объектов сцены. `full=true` для данных компонентов. |
| `/inspector` | `id` | Детальный обзор всех компонентов и полей объекта. |
| `/physics/raycast`| `origin, direction, distance` | Результат луча на сцене (объект, точка, нормаль). |
| `/screenshot_base64`| Нет | Захват изображения Scene View в формате base64. |
| `/filesystem/read`| `path` | Чтение содержимого файла (скрипты, json, ассеты). |
| `/filesystem/create_script`| `path, content` | Создание/обновление C# скрипта с авто-импортом. |
| `/modify` | `id, action, value` | Базовые правки: `rename`, `active`, `set_pos`, `layer`, `tag`. |
| `/create` | `type` | Создание примитивов, света, камеры или UI Canvas. |
| `/component` | `id, action, type, name, value, method, args` | `add`, `remove`, `set`, `get`, `invoke` (вызов методов). |
| `/transform` | `id, action, target` | `look_at` (к ID или Vector3), `align_with_view`, `reset`. |
| `/scene` | `action, id, path, parent` | `new`, `save`, `open`, `focus`, `duplicate`, `set_parent`. |
| `/asset` | `action, path, id, shader, name, from, to, type` | `create_material`, `apply_material`, `rename`, `move`, `find_by_type`. |
| `/asset_info` | `path` | GUID, тип, размер и список зависимостей ассета. |
| `/prefab` | `action, id, path` | `load` (инстанс), `save` (создать префаб), `unpack`. |
| `/editor` | `action, type, msg, pos, rot` | `window_open`, `notification`, `set_view` (управление камерой редактора). |
| `/selection` | `action, ids` | Геттер/сеттер выделенных объектов. |
| `/logs` | `count, filter` | Последние записи из Console (поддерживает фильтрацию). |
| `/diagnostics` | Нет | Поиск объектов с битыми ссылками на скрипты (Missing Script). |
| `/system` | Нет | Спецификации железа (CPU, GPU, VRAM, OS). |
| `/batch` | JSON body | Список команд для атомарного выполнения. |
| `/undo` / `/redo` | Нет | Отмена и повтор действий в редакторе. |

## ⚙️ Технические детали
- **Безопасность**: Принимает запросы только с `localhost`.
- **Потокобезопасность**: Все команды Unity API выполняются строго в главном потоке (Main Thread) через очередь.
- **Undo System**: Почти все операции (`modify`, `create`, `component`) поддерживают стандартный Ctrl+Z.
- **Версия**: API v3.0 (Marketing), Package v1.4.0.
