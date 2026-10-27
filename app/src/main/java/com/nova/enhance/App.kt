package com.nova.enhance

import android.app.Application

class App : Application() {
    override fun onCreate() {
        super.onCreate()
        ErrorLog.installCrashHandler(this)
    }
}
