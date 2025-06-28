package me.vbu.mirrordirectories;

import java.io.File;
import java.util.ArrayList;
import java.util.List;

/**
 * Utility class for comparing directories and finding differences.
 * Implemented as a singleton.
 */
public class DirectoryComparator {

    // Singleton instance
    private static DirectoryComparator instance;

    /**
     * Private constructor to prevent instantiation.
     */
    private DirectoryComparator() {
        // Private constructor to enforce singleton pattern
    }

    /**
     * Gets the singleton instance of DirectoryComparator.
     *
     * @return The singleton instance
     */
    public static synchronized DirectoryComparator getInstance() {
        if (instance == null) {
            instance = new DirectoryComparator();
        }
        return instance;
    }

    /**
     * Recursively compares directories and returns a list of paths that exist in
     * source but not in destination.
     *
     * @param sourceDir Source directory
     * @param destDir Destination directory
     * @param relativePath Current relative path for recursion
     * @return List of missing paths (relative to source root)
     */
    public List<String> compareDirectories(File sourceDir, File destDir, String relativePath) {
        List<String> missingItems = new ArrayList<>();
        File[] sourceContents = sourceDir.listFiles();

        if (sourceContents != null) {
            for (File sourceItem : sourceContents) {
                String currentRelativePath = relativePath.isEmpty() ?
                    sourceItem.getName() : relativePath + File.separator + sourceItem.getName();
                File destItem = new File(destDir, sourceItem.getName());

                if (!destItem.exists()) {
                    // Item doesn't exist in destination
                    missingItems.add(currentRelativePath);
                } else if (sourceItem.isDirectory() && destItem.isDirectory()) {
                    // Both are directories, recursively compare their contents
                    missingItems.addAll(compareDirectories(sourceItem, destItem, currentRelativePath));
                }
            }
        }

        return missingItems;
    }

    /**
     * Validates if the given path is a valid directory.
     *
     * @param directory The directory to validate
     * @return true if the path exists and is a directory, false otherwise
     */
    public boolean isValidDirectory(File directory) {
        return directory != null && directory.exists() && directory.isDirectory();
    }
}
