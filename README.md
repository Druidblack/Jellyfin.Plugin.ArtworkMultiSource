# ArtworkMultiSource

![logo](https://github.com/Druidblack/Jellyfin.Plugin.ArtworkMultiSource/blob/main/images/logot.jpg)

A plugin that receives images of posters and logos from themoviedb and tvdb sites and sorts them individually. 

The first images are in the language of the library, then in English.

By default, images are sorted by rating. But there is an option that can sort the images by full resolution (from higher to lower).

The plugin also creates a scheduled task with which you can periodically search for images using the specified parameters and get images in your native language or high quality (you need to set the time manually).


This plugin appeared to solve two problems. 

1. Get images in the desired language from several sources at once (removing dependence on the order of installed metadata providers).

2. After the library language, the themoviedb plugin produced images without text. And I wanted to receive posters and logos in English.

The images show how this plugin works.

![1](https://github.com/Druidblack/Jellyfin.Plugin.ArtworkMultiSource/blob/main/images/art.jpg)

![2](https://github.com/Druidblack/Jellyfin.Plugin.ArtworkMultiSource/blob/main/images/logo.jpg)

![3](https://github.com/Druidblack/Jellyfin.Plugin.ArtworkMultiSource/blob/main/images/poster.jpg)

![4](https://github.com/Druidblack/Jellyfin.Plugin.ArtworkMultiSource/blob/main/images/time.jpg)
