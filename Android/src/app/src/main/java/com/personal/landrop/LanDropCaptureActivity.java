package com.personal.landrop;

import com.journeyapps.barcodescanner.CaptureActivity;

/**
 * Uses the library's scanner implementation without its forced landscape
 * manifest configuration. Camera permission is requested by MainActivity
 * before this activity starts, which avoids OEM-specific permission races.
 */
public final class LanDropCaptureActivity extends CaptureActivity {
}
