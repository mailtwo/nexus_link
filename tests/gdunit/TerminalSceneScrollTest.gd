extends GdUnitTestSuite

const TERMINAL_SCENE_PATH := "res://scenes/TerminalScene.tscn"


func test_anchor_blank_space_is_removed_immediately_when_reaching_bottom() -> void:
	var runner := scene_runner(TERMINAL_SCENE_PATH)
	var scene := runner.scene() as Control
	assert_object(scene).is_not_null()

	scene.world_runtime = null
	runner.simulate_frames(2)
	runner.invoke("_clear_terminal_output")

	var output_scroll := scene.get_node("VBox/OutputScroll") as ScrollContainer
	var bottom_spacer := scene.get_node("VBox/OutputScroll/OutputContent/BottomSpacer") as Control
	var v_scroll := output_scroll.get_v_scroll_bar()
	assert_object(v_scroll).is_not_null()

	scene.has_pending_scroll_settle = false
	scene.is_motd_anchor_active = true
	scene.motd_anchor_blank_px = 120.0
	scene.motd_anchor_base_content_px = 10.0
	runner.invoke("_set_bottom_spacer_height", 120.0)

	output_scroll.scroll_vertical = int(v_scroll.max_value)
	runner.invoke("_on_output_scroll_value_changed", v_scroll.value)

	assert_bool(scene.is_motd_anchor_active).is_false()
	assert_bool(bottom_spacer.custom_minimum_size.y <= 0.5).is_true()
	assert_bool(scene.has_pending_scroll_settle).is_true()

	var next_lines: Array[String] = ["ls output"]
	runner.invoke("_append_output_batch", next_lines)
	runner.simulate_frames(2)
	assert_bool(bottom_spacer.custom_minimum_size.y <= 0.5).is_true()


func test_exit_editor_mode_requests_scroll_to_bottom() -> void:
	var runner := scene_runner(TERMINAL_SCENE_PATH)
	var scene := runner.scene() as Control
	assert_object(scene).is_not_null()

	scene.world_runtime = null
	runner.simulate_frames(2)
	scene.has_pending_scroll_settle = false

	runner.invoke("_open_editor_mode", "/tmp/test.ms", "print 1", false, "text", true)
	runner.simulate_frames(1)
	assert_bool(scene.editor_overlay.visible).is_true()

	scene.has_pending_scroll_settle = false
	runner.invoke("_exit_editor_mode")

	assert_bool(scene.editor_overlay.visible).is_false()
	assert_bool(scene.terminal_vbox.visible).is_true()
	assert_bool(scene.input_line.editable).is_true()
	assert_bool(scene.has_pending_scroll_settle).is_true()


func test_anchor_releases_when_growth_matches_blank_threshold() -> void:
	var runner := scene_runner(TERMINAL_SCENE_PATH)
	var scene := runner.scene() as Control
	assert_object(scene).is_not_null()

	scene.world_runtime = null
	runner.simulate_frames(2)
	runner.invoke("_clear_terminal_output")

	var lines: Array[String] = []
	for i in range(0, 40):
		lines.append("line %d" % i)
	runner.invoke("_append_output_batch", lines)
	runner.simulate_frames(4)

	var content_height := float(runner.invoke("_get_output_content_height"))
	var blank_px := 180.0
	scene.is_motd_anchor_active = true
	scene.motd_anchor_blank_px = blank_px
	scene.motd_anchor_base_content_px = content_height - blank_px
	runner.invoke("_set_bottom_spacer_height", blank_px)

	runner.invoke("_apply_default_scroll_policy_after_append")

	var bottom_spacer := scene.get_node("VBox/OutputScroll/OutputContent/BottomSpacer") as Control
	assert_bool(scene.is_motd_anchor_active).is_false()
	assert_bool(bottom_spacer.custom_minimum_size.y <= 0.5).is_true()
