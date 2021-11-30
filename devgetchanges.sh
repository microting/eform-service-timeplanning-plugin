#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-service-timeplanning-plugin/ServiceTimePlanningPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-service-timeplanning-plugin/ServiceTimePlanningPlugin
fi

cp -av Documents/workspace/microting/eform-debian-service/Plugins/ServiceTimePlanningPlugin Documents/workspace/microting/eform-service-timeplanning-plugin/ServiceTimePlanningPlugin
