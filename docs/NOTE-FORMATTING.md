# Note formatting

[← Back to README](../README.md)

Notes use a small markdown-style syntax. They look the same in the app and on the overlay.

## Text

| Write | Result |
|---|---|
| `**bold**` | **bold** |
| `*italic*` | *italic* |
| `__underline__` | underlined text |
| `~~strike~~` | ~~strike~~ |
| `==highlight==` | highlighted text |
| `` `code` `` | `code` |
| `[color=red]text[/color]` | red text (colour names or `#hex`) |
| `[hl=green]text[/hl]` | text on a green highlight |

## Blocks

```text
# Heading
## Smaller heading
- bullet
1. numbered item
- [ ] open task
- [x] done task
> quote
---                  (divider)
```

## Extras

- **Links**: paste a URL and it becomes clickable.
- **Timestamps**: `[21:30]` is drawn as a time badge.
- **Labels**: give a note coloured labels from its menu.
- **Images**: insert, paste (Ctrl+V) or drop a picture, then drag its corner to resize. Behind the scenes this is stored as:

  ```text
  ![caption](file.png =320)
  ```

  where `=320` is the display width in pixels.

## Example

```text
# Op order: [21:30]
**Objective:** hold the ==north gate==
- [ ] Brief squad
- [ ] Check radios on [color=#2f81f7]freq 44.1[/color]
> Fall back to rally point B if overrun
```
