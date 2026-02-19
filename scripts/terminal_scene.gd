extends Control

@export var max_scrollback_lines: int = 1000
@export var terminal_background_color: Color = Color(0.0470588, 0.0588235, 0.0705882, 1.0)
@export var terminal_text_color: Color = Color(0.839216, 0.976471, 0.858824, 1.0)
@export var terminal_caret_color: Color = Color(0.545098, 0.952941, 0.639216, 1.0)
@export var terminal_selection_color: Color = Color(0.22, 0.35, 0.24, 1.0)
@export var terminal_font_size: int = 18

var text_buffer: Array[String] = []

@onready var background: ColorRect = $Background
@onready var terminal_vbox: VBoxContainer = $VBox
@onready var output_scroll: ScrollContainer = $VBox/OutputScroll
@onready var output_label: RichTextLabel = $VBox/OutputScroll/Output
@onready var prompt_label: Label = $VBox/InputRow/Prompt
@onready var input_line: LineEdit = $VBox/InputRow/Input
@onready var editor_overlay: Control = $EditorOverlay
@onready var editor: CodeEdit = $EditorOverlay/Editor


func _ready() -> void:
	input_line.keep_editing_on_text_submit = true
	input_line.text_submitted.connect(_on_input_submitted)
	editor.gui_input.connect(_on_editor_gui_input)
	editor.shortcut_keys_enabled = true
	_apply_terminal_theme()
	input_line.grab_focus()
	_append_output("Terminal prototype ready.")


func _unhandled_input(event: InputEvent) -> void:
	if editor_overlay.visible:
		return
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		input_line.grab_focus()


func _on_input_submitted(command_text: String) -> void:
	var trimmed: String = command_text.strip_edges()
	if trimmed.is_empty():
		input_line.clear()
		input_line.call_deferred("grab_focus")
		return

	_append_output("%s%s" % [prompt_label.text, trimmed])
	if trimmed == "vim":
		_enter_editor_mode()
		input_line.clear()
		return

	input_line.clear()
	input_line.call_deferred("grab_focus")


func _append_output(line: String) -> void:
	text_buffer.append(line)
	if text_buffer.size() > max_scrollback_lines:
		text_buffer = text_buffer.slice(text_buffer.size() - max_scrollback_lines, text_buffer.size())
	output_label.text = "\n".join(text_buffer)
	call_deferred("_scroll_to_bottom")


func _scroll_to_bottom() -> void:
	var v_scroll := output_scroll.get_v_scroll_bar()
	if v_scroll:
		output_scroll.scroll_vertical = int(v_scroll.max_value)


func _apply_terminal_theme() -> void:
	background.color = terminal_background_color

	output_label.add_theme_color_override("default_color", terminal_text_color)
	prompt_label.add_theme_color_override("font_color", terminal_text_color)
	input_line.add_theme_color_override("font_color", terminal_text_color)
	input_line.add_theme_color_override("caret_color", terminal_caret_color)
	input_line.add_theme_color_override("selection_color", terminal_selection_color)

	editor.add_theme_color_override("font_color", terminal_text_color)
	editor.add_theme_color_override("caret_color", terminal_caret_color)
	editor.add_theme_color_override("selection_color", terminal_selection_color)
	editor.add_theme_color_override("background_color", terminal_background_color)

	output_label.add_theme_font_size_override("font_size", terminal_font_size)
	prompt_label.add_theme_font_size_override("font_size", terminal_font_size)
	input_line.add_theme_font_size_override("font_size", terminal_font_size)
	editor.add_theme_font_size_override("font_size", terminal_font_size)

	var shared_font: Font = input_line.get_theme_font("font")
	if shared_font:
		output_label.add_theme_font_override("font", shared_font)
		prompt_label.add_theme_font_override("font", shared_font)
		input_line.add_theme_font_override("font", shared_font)
		editor.add_theme_font_override("font", shared_font)


func _enter_editor_mode() -> void:
	editor_overlay.visible = true
	terminal_vbox.visible = false
	input_line.editable = false
	editor.grab_focus()


func _exit_editor_mode() -> void:
	editor_overlay.visible = false
	terminal_vbox.visible = true
	input_line.editable = true
	_append_output(_to_oneline_string(editor.text))
	input_line.call_deferred("grab_focus")


func _to_oneline_string(multiline_text: String) -> String:
	var normalized: String = multiline_text.replace("\r\n", "\n").replace("\r", "\n")
	return normalized.replace("\n", "\\n")


func _on_editor_gui_input(event: InputEvent) -> void:
	if not editor_overlay.visible:
		return
	if event is not InputEventKey:
		return

	var key_event := event as InputEventKey
	if not key_event.pressed or key_event.echo:
		return

	if key_event.keycode == KEY_ESCAPE:
		_exit_editor_mode()
		editor.accept_event()
		return

	if key_event.ctrl_pressed and key_event.keycode == KEY_S:
		_exit_editor_mode()
		editor.accept_event()
