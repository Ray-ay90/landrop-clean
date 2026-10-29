package com.personal.landrop;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.SystemClock;
import android.graphics.drawable.ColorDrawable;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.View;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.ScrollView;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;

/** Runs against a temporary editor view; no test note is added to saved records. */
public final class EditorScrollInstrumentation extends Instrumentation {
    private Activity activity;
    private EditText editor;
    private View scroll;
    private Object temporaryNote;
    private final StringBuilder results = new StringBuilder();

    @Override
    public void onCreate(Bundle arguments) {
        super.onCreate(arguments);
        start();
    }

    @Override
    public void onStart() {
        Bundle result = new Bundle();
        int code = Activity.RESULT_OK;
        try {
            Intent intent = new Intent(getTargetContext(), MainActivity.class);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
            activity = startActivitySync(intent);
            runOnMainSync(() -> {
                try {
                    Method theme = MainActivity.class.getDeclaredMethod("applyVisualTheme", String.class, boolean.class);
                    theme.setAccessible(true);
                    theme.invoke(activity, "Obsidian", true);
                    theme.invoke(activity, "Midnight", true);
                    theme.invoke(activity, "Silver", true);
                    Class<?> noteClass = Class.forName("com.personal.landrop.MainActivity$Note");
                    Constructor<?> constructor = noteClass.getDeclaredConstructor(
                            String.class, String.class, String.class, int.class, long.class);
                    constructor.setAccessible(true);
                    StringBuilder content = new StringBuilder("☐ Scroll test\n");
                    for (int i = 0; i < 160; i++) content.append("Line ").append(i).append(" - inertial scrolling\n");
                    Object note = constructor.newInstance("scroll-test", "Temporary scroll verification",
                            content.toString().trim(), 18, 0L);
                    temporaryNote = note;
                    Method open = MainActivity.class.getDeclaredMethod("openNoteEditor", noteClass);
                    open.setAccessible(true);
                    open.invoke(activity, note);
                    Field editorField = MainActivity.class.getDeclaredField("noteEditor");
                    editorField.setAccessible(true);
                    editor = (EditText) editorField.get(activity);
                    invokeActivity("applyNoteFontSize", new Class<?>[]{int.class}, 19);
                    invokeActivity("applyNoteLineSpacing", new Class<?>[]{float.class}, 1.5f);
                    invokeActivity("applyNoteLetterSpacing", new Class<?>[]{float.class}, 0.06f);
                    // Disable persistence before any gesture or edit is exercised.
                    setField(activity, "editingNote", null);
                    editor.setShowSoftInputOnFocus(false);
                    scroll = editor.getParent() instanceof ScrollView ? (View) editor.getParent() : editor;
                } catch (Exception error) {
                    throw new RuntimeException(error);
                }
            });
            waitForIdleSync();
            Field themeChoice = MainActivity.class.getDeclaredField("visualTheme");
            themeChoice.setAccessible(true);
            require("Silver".equals(themeChoice.get(activity)), "Legacy default and theme switch resolve to silver");
            require(androidx.core.view.WindowCompat.getInsetsController(activity.getWindow(), activity.getWindow().getDecorView()).isAppearanceLightStatusBars(), "Dark status icons on silver surface");
            require(Math.round(editor.getTextSize() / activity.getResources().getDisplayMetrics().scaledDensity) == 19,
                    "Continuous font size applies");
            require(Math.abs(editor.getLineSpacingMultiplier() - 1.5f) < 0.01f,
                    "Line spacing applies");
            require(Math.abs(editor.getLetterSpacing() - 0.06f) < 0.001f,
                    "Letter spacing applies");
            int textColor = editor.getCurrentTextColor();
            require(textColor == android.graphics.Color.rgb(45, 49, 58), "Silver editor uses readable graphite text");
            View editorPage = activity.findViewById(android.R.id.content);
            View page = editorPage instanceof android.view.ViewGroup
                    ? ((android.view.ViewGroup) editorPage).getChildAt(0) : editorPage;
            require(page.getBackground() instanceof ColorDrawable
                            && ((ColorDrawable) page.getBackground()).getColor() == android.graphics.Color.rgb(250, 250, 252),
                    "Silver editor uses neutral paper background");
            require(editor.getTextCursorDrawable() != null, "Explicit neutral cursor is configured");
            android.graphics.Bitmap screenshot = getUiAutomation().takeScreenshot();
            if (screenshot != null) try (java.io.FileOutputStream file = new java.io.FileOutputStream(new java.io.File(activity.getExternalFilesDir("fitness-test"), "silver-editor.png"))) {
                screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, file);
            }
            assertTypographyRoundTrip();
            resetScroll();
            assertFling(-1);
            assertFling(1);
            resetScroll();
            swipe(-1);
            SystemClock.sleep(80);
            int[] point = point(0.5f);
            long down = SystemClock.uptimeMillis();
            touch(down, MotionEvent.ACTION_DOWN, point[0], point[1]);
            int stopped = scrollY();
            SystemClock.sleep(160);
            require(Math.abs(scrollY() - stopped) < dp(3), "Touch stops inertia");
            touch(down, MotionEvent.ACTION_CANCEL, point[0], point[1]);
            resetScroll();
            String original = editor.getText().toString();
            runOnMainSync(() -> editor.setSelection(0));
            waitForIdleSync();
            int[] location = new int[2];
            runOnMainSync(() -> editor.getLocationOnScreen(location));
            int checkboxX = location[0] + editor.getTotalPaddingLeft() + dp(10);
            int checkboxY = location[1] + editor.getTotalPaddingTop() + editor.getLineHeight() / 2;
            down = SystemClock.uptimeMillis();
            touch(down, MotionEvent.ACTION_DOWN, checkboxX, checkboxY);
            touch(down, MotionEvent.ACTION_UP, checkboxX, checkboxY);
            require(editor.getText().charAt(0) == '☑', "Checklist tap still toggles");
            invokeEditor("performUndo");
            require(editor.getText().toString().equals(original), "Undo restores checklist");
            invokeEditor("performRedo");
            require(editor.getText().charAt(0) == '☑', "Redo restores checklist");
            invokeEditor("performUndo");
            runOnMainSync(() -> editor.getText().insert(2, "typed "));
            require(editor.getText().toString().startsWith("☐ typed "), "Text editing works");
            invokeEditor("performUndo");
            require(editor.getText().toString().equals(original), "Undo restores text");
            runOnMainSync(() -> {
                editor.setShowSoftInputOnFocus(true);
                editor.requestFocus();
                ((InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE))
                        .showSoftInput(editor, InputMethodManager.SHOW_IMPLICIT);
            });
            SystemClock.sleep(700);
            resetScroll();
            assertFling(-1);
            require(editor.getText().toString().equals(original), "Scrolling preserves content");
        } catch (Throwable error) {
            code = Activity.RESULT_CANCELED;
            results.append("FAIL: ").append(error).append('\n');
        } finally {
            if (activity != null) runOnMainSync(() -> activity.finish());
        }
        result.putString("stream", results.toString());
        finish(code, result);
    }

    private static void setField(Object target, String name, Object value) throws Exception {
        Field field = target.getClass().getDeclaredField(name);
        field.setAccessible(true);
        field.set(target, value);
    }

    private void invokeEditor(String name) {
        runOnMainSync(() -> {
            try {
                Method method = editor.getClass().getDeclaredMethod(name);
                method.setAccessible(true);
                method.invoke(editor);
            } catch (Exception error) {
                throw new RuntimeException(error);
            }
        });
        waitForIdleSync();
    }

    private void invokeActivity(String name, Class<?>[] parameterTypes, Object value) throws Exception {
        Method method = MainActivity.class.getDeclaredMethod(name, parameterTypes);
        method.setAccessible(true);
        method.invoke(activity, value);
    }

    private void assertTypographyRoundTrip() throws Exception {
        ArrayList<Object> source = new ArrayList<>();
        source.add(temporaryNote);
        Method write = MainActivity.class.getDeclaredMethod("notesToJson", java.util.List.class);
        write.setAccessible(true);
        Object json = write.invoke(activity, source);
        ArrayList<Object> restored = new ArrayList<>();
        Method read = MainActivity.class.getDeclaredMethod("readNotes", String.class, java.util.List.class);
        read.setAccessible(true);
        read.invoke(activity, json.toString(), restored);
        require(restored.size() == 1, "Typography record round-trip restores note");
        Object note = restored.get(0);
        require((int) readField(note, "fontSize") == 19, "Font size persists");
        require(Math.abs((float) readField(note, "lineSpacing") - 1.5f) < 0.01f,
                "Line spacing persists");
        require(Math.abs((float) readField(note, "letterSpacing") - 0.06f) < 0.001f,
                "Letter spacing persists");
    }

    private static Object readField(Object target, String name) throws Exception {
        Field field = target.getClass().getDeclaredField(name);
        field.setAccessible(true);
        return field.get(target);
    }

    private void resetScroll() {
        runOnMainSync(() -> editor.setSelection(0));
        waitForIdleSync();
        runOnMainSync(() -> scroll.scrollTo(0, 0));
        SystemClock.sleep(120);
    }

    private int dp(int value) {
        return Math.round(value * activity.getResources().getDisplayMetrics().density);
    }

    private int scrollY() {
        int[] value = new int[1];
        runOnMainSync(() -> value[0] = scroll.getScrollY());
        return value[0];
    }

    private int[] point(float fraction) {
        int[] location = new int[2];
        runOnMainSync(() -> {
            scroll.getLocationOnScreen(location);
            location[0] += scroll.getWidth() / 2;
            location[1] += Math.round(scroll.getHeight() * fraction);
        });
        return location;
    }

    private void swipe(int direction) {
        int[] start = point(direction < 0 ? 0.75f : 0.25f);
        int[] end = point(direction < 0 ? 0.25f : 0.75f);
        long down = SystemClock.uptimeMillis();
        touch(down, MotionEvent.ACTION_DOWN, start[0], start[1]);
        for (int step = 1; step <= 8; step++) {
            SystemClock.sleep(16);
            touch(down, MotionEvent.ACTION_MOVE, start[0], start[1] + (end[1] - start[1]) * step / 8);
        }
        touch(down, MotionEvent.ACTION_UP, end[0], end[1]);
    }

    private void touch(long down, int action, int x, int y) {
        MotionEvent event = MotionEvent.obtain(down, SystemClock.uptimeMillis(), action, x, y, 0);
        event.setSource(InputDevice.SOURCE_TOUCHSCREEN);
        sendPointerSync(event);
        event.recycle();
    }

    private void assertFling(int direction) {
        swipe(direction);
        int released = scrollY();
        SystemClock.sleep(200);
        int after = scrollY();
        require((after - released) * -direction > dp(10),
                "Inertia direction " + direction + ": " + released + " -> " + after);
        SystemClock.sleep(1100);
    }

    private void require(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
        results.append("PASS: ").append(message).append('\n');
    }
}
