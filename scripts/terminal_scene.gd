extends Control

@export var max_scrollback_lines: int = 1000

var text_buffer: Array[String] = []

@onready var output_scroll: ScrollContainer = $VBox/OutputScroll
@onready var output_label: RichTextLabel = $VBox/OutputScroll/Output
@onready var prompt_label: Label = $VBox/InputRow/Prompt
@onready var input_line: LineEdit = $VBox/InputRow/Input
@onready var editor_overlay: Control = $EditorOverlay


func _ready() -> void:
	input_line.text_submitted.connect(_on_input_submitted)
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
		input_line.grab_focus()
		return

	_append_output("%s%s" % [prompt_label.text, trimmed])
	input_line.clear()
	input_line.grab_focus()


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
